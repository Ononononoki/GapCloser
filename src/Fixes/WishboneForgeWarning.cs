using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace GapCloser.Fixes
{
    // preview the next refinement without consuming its seed or changing the shared RNG state
    internal static class WishboneForgeWarning
    {
        private const string Name = "WishboneForgeWarning";
        private const string WishboneName = "$item_wishbone";
        private const string Warning = "<color=orange>Your wishbone trembles. It senses this item's impending destruction.</color>";
        // one ping a second with the game's own wishbone effects, its sound at a share of the ping's volume
        private const float PulseSeconds = 1f;
        private const float PulseVolume = 0.3f;
        // a ping effect stays about 4 s, anything left after this is removed
        private const float EffectSeconds = 6f;
        // the glow behind the button label jumps up with each ping and fades until the next one
        private const float GlowInset = 4f;
        private const float GlowMin = 0.06f;
        private const float GlowMax = 0.36f;
        private static readonly Color GlowColor = new Color(0.88f, 0.24f, 0.08f);
        // while the upgrade of an item that will be destroyed runs, the ping and the glow on the progress bar are faster, redder and never fade out
        private const float CriticalSeconds = 0.35f;
        private const float CriticalMin = 0.2f;
        private static readonly Color CriticalColor = new Color(0.95f, 0.1f, 0.05f);
        private static readonly AccessTools.FieldRef<Player, Dictionary<int, int>> Seeds =
            AccessTools.FieldRefAccess<Player, Dictionary<int, int>>("m_itemUpgradeSeeds");
        private static readonly Func<InventoryGui, Recipe> SelectedRecipe = SelectedGetter<Recipe>("Recipe");
        private static readonly Func<InventoryGui, ItemDrop.ItemData> SelectedItem = SelectedGetter<ItemDrop.ItemData>("ItemData");
        private static readonly AccessTools.FieldRef<UITooltip, RectTransform> Anchor = AccessTools.FieldRefAccess<UITooltip, RectTransform>("m_anchor");
        private static readonly AccessTools.FieldRef<UITooltip, Vector2> FixedPosition = AccessTools.FieldRefAccess<UITooltip, Vector2>("m_fixedPosition");
        private static readonly AccessTools.FieldRef<InventoryGui, float> CraftTimer = AccessTools.FieldRefAccess<InventoryGui, float>("m_craftTimer");
        private static readonly AccessTools.FieldRef<InventoryGui, Recipe> CraftRecipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
        private static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData> CraftItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem");
        private static ConfigEntry<bool> _enabled;
        private static bool _loggedWarn;
        private static Image _glow, _barGlow;
        private static float _lastPulse = float.NegativeInfinity;
        private static bool _pingFailed, _loggedPing;
        private static bool _failed;
        private static readonly System.Reflection.MethodInfo UpgradeRoll = AccessTools.Method(typeof(Player), "GetItemUpgradeChance");

        // the code of the game's seed draw, craft result and recipe ui that the prediction was checked against (CodeCheck)
        private static readonly string[] CheckedSeedCode = { "21DCADD7" };
        private static readonly string[] CheckedCraftCode = { "9F13AA94" };
        private static readonly string[] CheckedRecipeCode = { "D2C6E01F" };

        // every method is checked and every mismatch is logged, ForceOnUntestedVersions is not read here
        private static bool CheckGameCode()
        {
            bool valid = CheckMethod(typeof(Player), "GetItemUpgradeChance", CheckedSeedCode);
            valid &= CheckMethod(typeof(InventoryGui), "DoCrafting", CheckedCraftCode);
            valid &= CheckMethod(typeof(InventoryGui), "UpdateRecipe", CheckedRecipeCode);
            return valid;
        }

        private static bool CheckMethod(Type type, string methodName, string[] known)
        {
            string actual = CodeCheck.Of(AccessTools.Method(type, methodName));
            if (actual != null && Array.IndexOf(known, actual) >= 0) return true;
            Plugin.Log.LogWarning($"{Name}: off, {type.Name}.{methodName} is not the checked code ({actual ?? "unreadable"}, checked {string.Join(", ", known)}). " +
                "The prediction can not be verified for this game code and ForceOnUntestedVersions does not turn it on. Revalidate it first.");
            return false;
        }

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _enabled = config.Bind(Sections.Options, Name, true,
                "An equipped Wishbone warns on the Forge of Potential refinement button when the selected item's next attempt will destroy it. Previewing never consumes an attempt.");
            if (!Plugin.GameMatches(Name)) return;
            // a prediction needs the checked game code even when other fixes are forced on
            if (!CheckGameCode()) return;
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "UpdateRecipe"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(WishboneForgeWarning), nameof(Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(WishboneForgeWarning), nameof(Postfix))) { priority = Priority.Last });
            Plugin.Log.LogInfo($"{Name}: ready, requires an equipped Wishbone");
        }

        private static void Prefix(InventoryGui __instance, out string __state)
        {
            var tooltip = __instance.m_craftButton.GetComponent<UITooltip>();
            __state = tooltip != null ? tooltip.m_text : null;
        }

        private static void Postfix(InventoryGui __instance, Player player, string __state)
        {
            var tooltip = __instance.m_craftButton.GetComponent<UITooltip>();
            if (tooltip == null) return;
            ItemDrop.ItemData wishbone = null;
            bool warn = false, crafting = false;
            try
            {
                if (_enabled.Value && !_failed && player != null && player == Player.m_localPlayer)
                {
                    var station = player.GetCurrentCraftingStation();
                    if (station != null && station.m_upgrader)
                    {
                        wishbone = FindEquippedWishbone(player.GetInventory().GetAllItems());
                        // while the upgrade bar runs the game works on its own copy of the pair
                        crafting = CraftTimer(__instance) >= 0f;
                        var selected = crafting ? CraftRecipe(__instance) : SelectedRecipe(__instance);
                        var item = crafting ? CraftItem(__instance) : SelectedItem(__instance);
                        if (wishbone != null && selected != null && item != null)
                        {
                            // a replacement of the game's seed draw cannot be predicted with the vanilla algorithm
                            var patches = Harmony.GetPatchInfo(UpgradeRoll);
                            if (patches != null && patches.Owners.Count > 0)
                                throw new InvalidOperationException("another mod patches Player.GetItemUpgradeChance");
                            var idol = FindIdol(selected.m_resources);
                            if (idol != null)
                            {
                                int key = unchecked(selected.m_item.gameObject.name.GetStableHashCode() + item.m_quality + 1);
                                int seed = GetSeed(Seeds(player), player.GetPlayerName(), key);
                                warn = WillBreak(PeekRoll(seed), idol.m_upgradeChance, idol.m_breakChance);
                                if (warn && !_loggedWarn)
                                {
                                    _loggedWarn = true;
                                    Plugin.Log.LogInfo($"{Name}: warned that {item.m_shared.m_name} level {item.m_quality} would be destroyed by the next attempt, first time this session");
                                }
                            }
                        }
                    }
                }
                bool critical = warn && crafting;
                if (warn && PingDue(Time.unscaledTime, _lastPulse, critical ? CriticalSeconds : PulseSeconds))
                {
                    _lastPulse = Time.unscaledTime;
                    if (!_pingFailed)
                    {
                        try
                        {
                            PlayPing(wishbone, player);
                        }
                        catch (Exception error)
                        {
                            _pingFailed = true;
                            Plugin.Log.LogWarning($"{Name}: the wishbone ping is off for this session: {error.Message}");
                        }
                    }
                }
                UpdateGlow(ref _glow, __instance.m_craftButton.transform, warn && !crafting, false, GlowInset);
                // the bar glow sits on the fill of the upgrade bar, so it grows with the progress and the empty part stays as it is
                var fill = __instance.m_craftProgressBar != null ? __instance.m_craftProgressBar.m_bar : null;
                if (fill != null) UpdateGlow(ref _barGlow, fill, critical, true, 0f);
            }
            catch (Exception error)
            {
                warn = false;
                if (_glow != null) _glow.gameObject.SetActive(false);
                if (_barGlow != null) _barGlow.gameObject.SetActive(false);
                if (!_failed) Plugin.Log.LogWarning($"{Name}: prediction disabled for this session: {error.Message}");
                _failed = true;
            }
            string text = AppendWarning(tooltip.m_text, warn);
            // the tooltip is refreshed only when the warning appears or goes away
            bool had = __state != null && __state.Contains(Warning);
            try
            {
                // vanilla assigns m_text directly, so restore the previous value before Set refreshes an open tooltip
                if (warn != had)
                {
                    tooltip.m_text = __state;
                    tooltip.Set(tooltip.m_topic, text, Anchor(tooltip), FixedPosition(tooltip));
                    if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(tooltip.m_topic) &&
                        __state != null && __state.Contains(Warning) &&
                        ReferenceEquals(AccessTools.Field(typeof(UITooltip), "m_current").GetValue(null), tooltip))
                        UITooltip.HideTooltip();
                }
                else tooltip.m_text = text;
            }
            catch (Exception error)
            {
                if (!_failed) Plugin.Log.LogWarning($"{Name}: tooltip refresh failed, prediction disabled for this session: {error.Message}");
                _failed = true;
            }
        }

        internal static ItemDrop.ItemData FindEquippedWishbone(List<ItemDrop.ItemData> items)
        {
            foreach (var item in items)
                if (item.m_equipped && item.m_shared.m_name == WishboneName) return item;
            return null;
        }

        // the selected pair is a private value type, so read its getters without boxing every UI update
        private static Func<InventoryGui, T> SelectedGetter<T>(string property)
        {
            var field = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
            var method = new DynamicMethod(Name + property, typeof(T), new[] { typeof(InventoryGui) }, typeof(WishboneForgeWarning), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldflda, field);
            il.Emit(OpCodes.Call, AccessTools.PropertyGetter(field.FieldType, property));
            il.Emit(OpCodes.Ret);
            return (Func<InventoryGui, T>)method.CreateDelegate(typeof(Func<InventoryGui, T>));
        }

        private static ItemDrop.ItemData.SharedData FindIdol(Piece.Requirement[] requirements)
        {
            // the game takes the first upgrader resource from the selected recipe
            foreach (var requirement in requirements)
                if (requirement.m_upgraderResource)
                    return requirement.m_resItem != null ? requirement.m_resItem.m_itemData.m_shared : null;
            return null;
        }

        internal static int GetSeed(Dictionary<int, int> seeds, string playerName, int key)
        {
            return seeds.TryGetValue(key, out int seed) ? seed : unchecked(playerName.GetStableHashCode() + key);
        }

        internal static float PeekRoll(int seed)
        {
            var state = Random.state;
            try
            {
                Random.InitState(seed);
                return Random.value;
            }
            finally
            {
                Random.state = state;
            }
        }

        internal static bool WillBreak(float roll, float upgradeChance, float breakChance)
        {
            return roll > upgradeChance && breakChance >= 1f - roll;
        }

        internal static string AppendWarning(string text, bool warn)
        {
            // remove only our own line if another UI patch retained the previous tooltip
            text = (text ?? "").Replace("\n\n" + Warning, "").Replace(Warning, "");
            return !warn ? text : string.IsNullOrEmpty(text) ? Warning : text + "\n\n" + Warning;
        }

        internal static bool PingDue(float now, float last, float interval)
        {
            return now - last >= interval;
        }

        // the wishbone's own ping effect and sound, made on this client only so that nobody else sees or hears it
        private static void PlayPing(ItemDrop.ItemData wishbone, Player player)
        {
            var finder = wishbone.m_shared.m_equipStatusEffect as SE_Finder;
            if (finder == null || finder.m_pingEffectNear == null) return;
            bool kept = ZNetView.m_forceDisableInit;
            GameObject[] effects;
            ZNetView.m_forceDisableInit = true;
            try
            {
                effects = finder.m_pingEffectNear.Create(player.transform.position, player.transform.rotation, player.transform, 1f, -1, player.GetZDOID());
            }
            finally
            {
                ZNetView.m_forceDisableInit = kept;
            }
            if (effects == null) return;
            foreach (var effect in effects)
            {
                if (effect == null) continue;
                foreach (var sound in effect.GetComponentsInChildren<ZSFX>(true)) Quiet(sound);
                UnityEngine.Object.Destroy(effect, EffectSeconds);
            }
            if (_loggedPing) return;
            _loggedPing = true;
            Plugin.Log.LogInfo($"{Name}: played the wishbone ping ({effects.Length} effects, sound at {PulseVolume * 100f:0}%), first time this session");
        }

        // the game starts a sound in its first update, so its volume is lowered before that
        internal static void Quiet(ZSFX sound)
        {
            sound.m_minVol *= PulseVolume;
            sound.m_maxVol *= PulseVolume;
        }

        // a glow image as the first child of the target, so whatever the target holds is drawn over it
        private static void UpdateGlow(ref Image glow, Transform target, bool on, bool critical, float inset)
        {
            if (!on)
            {
                if (glow != null) glow.gameObject.SetActive(false);
                return;
            }
            if (glow == null || glow.transform.parent != target)
            {
                if (glow != null) glow.gameObject.SetActive(false);
                var made = new GameObject(Name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                made.transform.SetParent(target, false);
                made.transform.SetAsFirstSibling();
                glow = made.GetComponent<Image>();
                glow.raycastTarget = false;
                var rect = glow.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(inset, inset);
                rect.offsetMax = new Vector2(-inset, -inset);
            }
            float fade = 1f - Mathf.Clamp01((Time.unscaledTime - _lastPulse) / (critical ? CriticalSeconds : PulseSeconds));
            var color = critical ? CriticalColor : GlowColor;
            color.a = Mathf.Lerp(critical ? CriticalMin : GlowMin, GlowMax, fade * fade);
            glow.color = color;
            glow.gameObject.SetActive(true);
        }
    }
}
