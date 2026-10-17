using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // AzuCraftyBoxes 1.8.19 checks every requirement of every piece in the hammer's list against every chest in range, one
    // chest at a time: 33,363 chest counts for one list of 499 pieces, 182 ms in one frame, each time the hammer comes out,
    // the tab changes or the inventory changes. this answers its check with the same rules in the same order, but walks
    // the chests once per material a frame and keeps that material's total for the rest of the frame. the answers are the
    // same: its chest rules and counts depend only on the chest and the material, and every inventory change starts over.
    // nothing kept is used while crafting or building takes items, and any error leaves the check to AzuCraftyBoxes
    internal static class AzuCraftyBoxesBuildCounts
    {
        private const string Name = "AzuCraftyBoxesBuildCounts";
        private const string PatchType = "AzuCraftyBoxes.Patches.HaveRequirementsPatch2";
        // the size of the check's code as read in 1.8.19: another build of it stands the fix down
        private const int CheckedCodeBytes = 1016;
        private const float StatSeconds = 60f;
        // with the self-check on, every this many checks AzuCraftyBoxes answers too and the two are compared
        private const int SelfCheckEvery = 20;
        private const int DifferencesLogged = 5;
        private static readonly string[] CheckedVersions = { "1.8.19" };

        // AzuCraftyBoxes' own methods and settings, called as its check calls them
        private static Func<bool> _shouldPrevent, _skip;
        private static Func<ItemDrop, bool> _integrity;
        private static Func<Player, float, IList> _nearby;
        private static ConfigEntry<float> _range;
        private static Func<object, string> _chestPrefab;
        private static Func<string, string, string, bool> _canPull;
        private static Func<object, string, int> _count;
        private static Func<int, int> _decrement;
        private static ConfigEntry<bool> _selfCheck;

        // per material this frame: the pulled total (can build) and whether any chest has one (can almost build)
        private static readonly Dictionary<ItemDrop, int> Totals = new Dictionary<ItemDrop, int>(new SameItem());
        private static readonly Dictionary<ItemDrop, bool> Founds = new Dictionary<ItemDrop, bool>(new SameItem());
        private static int _frame = -1, _taking;
        private static IList _list;
        private static bool _changed, _loggedFirst, _loggedError;
        private static int _answered, _walks, _counts, _match, _different;
        private static float _nextStat;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Sections.Fixes, Name, true,
                "AzuCraftyBoxes 1.8.19: the hammer's build list counts each material in the chests once a frame instead of again for every piece. Same answers, much less work near many chests.");
            _selfCheck = config.Bind(Sections.Debug, Name + "SelfCheck", false,
                "Test: every 20th hammer build check AzuCraftyBoxes answers too, the log counts MATCH and DIFFERENT.");
            if (!enabled.Value || !Plugin.TargetMatches(Name, AzuCraftyBoxesForgeRecipes.TargetGuid, CheckedVersions)) return;
            var check = AccessTools.TypeByName(PatchType);
            var postfix = check == null ? null : AccessTools.Method(check, "Postfix");
            if (postfix == null || !SameShape(postfix))
            {
                Plugin.Log.LogWarning($"{Name}: AzuCraftyBoxes' build check is not the one this was written for, the fix stays off");
                return;
            }
            if (!Resolve())
            {
                Plugin.Log.LogWarning($"{Name}: a method or setting AzuCraftyBoxes' build check uses was not found, the fix stays off");
                return;
            }
            harmony.Patch(postfix, prefix: Method(nameof(Prefix)), postfix: Method(nameof(Compare)));
            harmony.Patch(AccessTools.Method(typeof(Inventory), "Changed"), postfix: Method(nameof(Changed)));
            var takeStart = new HarmonyMethod(AccessTools.Method(typeof(AzuCraftyBoxesBuildCounts), nameof(TakeStart))) { priority = Priority.First };
            var takeEnd = Method(nameof(TakeEnd));
            harmony.Patch(AccessTools.Method(typeof(Player), "ConsumeResources"), prefix: takeStart, finalizer: takeEnd);
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "DoCrafting"), prefix: takeStart, finalizer: takeEnd);
            Plugin.Log.LogInfo($"{Name}: on for AzuCraftyBoxes 1.8.19{(_selfCheck.Value ? ", self-check on" : "")}");
        }

        private static HarmonyMethod Method(string name) => new HarmonyMethod(AccessTools.Method(typeof(AzuCraftyBoxesBuildCounts), name));

        // its arguments and its code size as read
        private static bool SameShape(MethodInfo postfix)
        {
            var p = postfix.GetParameters();
            if (p.Length != 6 || p[0].ParameterType != typeof(Player) || p[1].ParameterType != typeof(bool).MakeByRefType() || p[2].ParameterType != typeof(Piece) ||
                p[3].ParameterType != typeof(Player.RequirementMode) || p[4].ParameterType != typeof(HashSet<string>) || p[5].ParameterType != typeof(Dictionary<string, int>)) return false;
            var body = postfix.GetMethodBody();
            return body != null && body.GetILAsByteArray().Length == CheckedCodeBytes;
        }

        private static bool Resolve()
        {
            var misc = AccessTools.TypeByName("AzuCraftyBoxes.Util.Functions.MiscFunctions");
            var plugin = AccessTools.TypeByName("AzuCraftyBoxes.AzuCraftyBoxesPlugin");
            var boxes = AccessTools.TypeByName("AzuCraftyBoxes.Util.Functions.Boxes");
            var frame = boxes == null ? null : AccessTools.Inner(boxes, "QueryFrame");
            var container = AccessTools.TypeByName("AzuCraftyBoxes.IContainers.IContainer");
            var extensions = AccessTools.TypeByName("AzuCraftyBoxes.IContainers.IContainerExtensions");
            if (misc == null || plugin == null || boxes == null || frame == null || container == null || extensions == null) return false;
            var prevent = AccessTools.Method(misc, "ShouldPrevent", Type.EmptyTypes);
            var integrity = AccessTools.Method(misc, "CheckItemDropIntegrity", new[] { typeof(ItemDrop) });
            var skip = AccessTools.Field(plugin, "skip");
            var range = AccessTools.Field(plugin, "mRange");
            var get = AccessTools.Method(frame, "Get");
            var canPull = AccessTools.Method(boxes, "CanItemBePulled", new[] { typeof(string), typeof(string), typeof(string) });
            var decrement = AccessTools.Method(boxes, "CheckAndDecrement", new[] { typeof(int) });
            var prefabName = AccessTools.Method(container, "GetPrefabName", Type.EmptyTypes);
            var contains = AccessTools.Method(extensions, "ContainsItem", new[] { container, typeof(string), typeof(int), typeof(int).MakeByRefType() });
            if (prevent == null || integrity == null || skip == null || skip.FieldType != typeof(bool) || !skip.IsStatic || range == null || !range.IsStatic ||
                get == null || !get.IsGenericMethodDefinition || canPull == null || decrement == null || prefabName == null || contains == null) return false;
            // the plugin's own setting object, made in its Awake (it loads before this)
            _range = range.GetValue(null) as ConfigEntry<float>;
            if (_range == null) return false;
            _shouldPrevent = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), prevent);
            _integrity = (Func<ItemDrop, bool>)Delegate.CreateDelegate(typeof(Func<ItemDrop, bool>), integrity);
            _canPull = (Func<string, string, string, bool>)Delegate.CreateDelegate(typeof(Func<string, string, string, bool>), canPull);
            _decrement = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), decrement);
            _skip = ReadStatic(skip);
            _nearby = NearbyCall(get.MakeGenericMethod(typeof(Player)));
            _chestPrefab = PrefabCall(container, prefabName);
            _count = CountCall(container, contains);
            return true;
        }

        private static Func<bool> ReadStatic(FieldInfo field)
        {
            var dm = new DynamicMethod("GapCloser_" + field.Name, typeof(bool), Type.EmptyTypes, typeof(AzuCraftyBoxesBuildCounts).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<bool>)dm.CreateDelegate(typeof(Func<bool>));
        }

        // QueryFrame.Get<Player>(player, range): the chest list of this frame
        private static Func<Player, float, IList> NearbyCall(MethodInfo get)
        {
            var dm = new DynamicMethod("GapCloser_NearbyChests", typeof(IList), new[] { typeof(Player), typeof(float) }, typeof(AzuCraftyBoxesBuildCounts).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, get);
            il.Emit(OpCodes.Ret);
            return (Func<Player, float, IList>)dm.CreateDelegate(typeof(Func<Player, float, IList>));
        }

        // chest.GetPrefabName()
        private static Func<object, string> PrefabCall(Type container, MethodInfo prefabName)
        {
            var dm = new DynamicMethod("GapCloser_ChestPrefab", typeof(string), new[] { typeof(object) }, typeof(AzuCraftyBoxesBuildCounts).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, container);
            il.Emit(OpCodes.Callvirt, prefabName);
            il.Emit(OpCodes.Ret);
            return (Func<object, string>)dm.CreateDelegate(typeof(Func<object, string>));
        }

        // chest.ContainsItem(name, 1, out count), the count, as its check calls it
        private static Func<object, string, int> CountCall(Type container, MethodInfo contains)
        {
            var dm = new DynamicMethod("GapCloser_ChestCount", typeof(int), new[] { typeof(object), typeof(string) }, typeof(AzuCraftyBoxesBuildCounts).Module, true);
            var il = dm.GetILGenerator();
            var count = il.DeclareLocal(typeof(int));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, container);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ldloca_S, count);
            il.Emit(OpCodes.Call, contains);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldloc, count);
            il.Emit(OpCodes.Ret);
            return (Func<object, string, int>)dm.CreateDelegate(typeof(Func<object, string, int>));
        }

        private static void TakeStart() => _taking++;

        private static void TakeEnd()
        {
            if (--_taking < 0) _taking = 0;
            _changed = true;
        }

        private static void Changed() => _changed = true;

        // its arguments by position (its own are named __instance and __result). false: answered here, its code is skipped
        private static bool Prefix(Player __0, ref bool __1, Piece __2, Player.RequirementMode __3, HashSet<string> __4, Dictionary<string, int> __5, out int __state)
        {
            __state = -1;
            if (_taking > 0) return true;
            bool result = __1;
            try
            {
                Answer(__0, ref result, __2, __3, __4, __5);
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Plugin.Log.LogWarning($"{Name}: a build check failed here ({e.GetType().Name}: {e.Message}), AzuCraftyBoxes answers it, first time this session");
                }
                return true;
            }
            _answered++;
            Stat();
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Plugin.Log.LogInfo($"{Name}: answered a build check from the material totals, first time this session");
            }
            if (_selfCheck.Value && _answered % SelfCheckEvery == 0)
            {
                __state = result ? 1 : 0;
                return true;
            }
            __1 = result;
            return false;
        }

        // the self-check: AzuCraftyBoxes answered too
        private static void Compare(ref bool __1, Piece __2, Player.RequirementMode __3, int __state)
        {
            if (__state < 0) return;
            bool ours = __state == 1;
            if (ours == __1)
            {
                _match++;
                return;
            }
            _different++;
            if (_different <= DifferencesLogged)
                Plugin.Log.LogWarning($"{Name}: self-check DIFFERENT for {(__2 != null ? __2.name : "?")} ({__3}): here {ours}, AzuCraftyBoxes {__1}");
        }

        // AzuCraftyBoxes' check in its order (read from its code): the same gates, then each requirement, the chests
        // only for what the player lacks. result stays as it was where its check returns early
        private static void Answer(Player player, ref bool result, Piece piece, Player.RequirementMode mode, HashSet<string> knownMaterial, Dictionary<string, int> knownStations)
        {
            if (_shouldPrevent() | result) return;
            if (_skip()) return;
            if ((object)player == null || (object)player.transform == null) return;
            if (piece == null) return;
            var station = piece.m_craftingStation;
            if (station)
            {
                if ((uint)(mode - 1) <= 1u)
                {
                    if (!knownStations.ContainsKey(station.m_name)) return;
                }
                else if (!CraftingStation.HaveBuildStationInRange(station.m_name, player.transform.position)) return;
            }
            if (piece.m_dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(piece.m_dlc)) return;
            var chests = _nearby(player, _range.Value);
            Fresh(chests);
            foreach (var requirement in piece.m_resources)
            {
                var item = requirement.m_resItem;
                if (item == null || requirement.m_amount <= 0 || !_integrity(item)) continue;
                item.m_itemData.m_dropPrefab = item.gameObject;
                if (item.m_itemData.m_dropPrefab == null) continue;
                string name = item.m_itemData.m_shared.m_name;
                switch ((int)mode)
                {
                    case 0:
                        int have = player.GetInventory().CountItems(name, -1, true);
                        if (have >= requirement.m_amount) continue;
                        if (have + Total(item, name, chests) < requirement.m_amount) return;
                        continue;
                    case 1:
                        if (!knownMaterial.Contains(name)) return;
                        continue;
                    case 2:
                        if (player.GetInventory().HaveItem(name, true)) continue;
                        if (!Found(item, name, chests)) return;
                        continue;
                }
            }
            result = true;
        }

        // a new frame, another chest list or any inventory change: count again
        private static void Fresh(IList chests)
        {
            int frame = Time.frameCount;
            if (frame == _frame && ReferenceEquals(chests, _list) && !_changed) return;
            _frame = frame;
            _list = chests;
            _changed = false;
            Totals.Clear();
            Founds.Clear();
        }

        // what the chests add for a build: each chest its rules let pull from, in list order, counted less one when its
        // keep-one setting is on. its check stops at an error in a chest's name or rules (its catch is around the count
        // only), so the total ends there; a count that fails leaves that chest out, as there. every count is 0 or more, so
        // a total compared with what is needed answers as its walk that stops once enough is found
        private static int Total(ItemDrop item, string name, IList chests)
        {
            if (Totals.TryGetValue(item, out int total)) return total;
            _walks++;
            string prefab = item.name;
            total = TotalOf(chests, chest => _canPull(_chestPrefab(chest), prefab, ""), chest => _count(chest, name), _decrement);
            Totals[item] = total;
            return total;
        }

        // whether a chest its rules let pull from has one, the first in list order; an error ends its walk with no
        private static bool Found(ItemDrop item, string name, IList chests)
        {
            if (Founds.TryGetValue(item, out bool found)) return found;
            _walks++;
            string prefab = Utils.GetPrefabName(item.m_itemData.m_dropPrefab);
            found = AnyOf(chests, chest => _canPull(_chestPrefab(chest), prefab, ""), chest => _count(chest, name));
            Founds[item] = found;
            return found;
        }

        // the two walks, apart from the game's types so the sandbox can run them against a copy of AzuCraftyBoxes' loops
        internal static int TotalOf(IList chests, Func<object, bool> pull, Func<object, int> count, Func<int, int> decrement)
        {
            int total = 0;
            for (int i = 0; i < chests.Count; i++)
            {
                var chest = chests[i];
                bool can;
                try
                {
                    can = pull(chest);
                }
                catch (Exception)
                {
                    break;
                }
                if (!can) continue;
                _counts++;
                try
                {
                    total += decrement(count(chest));
                }
                catch (Exception)
                {
                }
            }
            return total;
        }

        internal static bool AnyOf(IList chests, Func<object, bool> pull, Func<object, int> count)
        {
            try
            {
                for (int i = 0; i < chests.Count; i++)
                {
                    var chest = chests[i];
                    if (!pull(chest)) continue;
                    _counts++;
                    if (count(chest) >= 1) return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static void Stat()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextStat) return;
            if (_nextStat > 0f)
                Plugin.Log.LogInfo($"{Name} {StatSeconds:0} s: {_answered} build checks answered, {_walks} material walks, {_counts} chest counts" +
                                   (_selfCheck.Value ? $"; self-check {_match} MATCH, {_different} DIFFERENT" : ""));
            _nextStat = now + StatSeconds;
            _answered = _walks = _counts = 0;
        }

        private sealed class SameItem : IEqualityComparer<ItemDrop>
        {
            public bool Equals(ItemDrop a, ItemDrop b) => ReferenceEquals(a, b);
            public int GetHashCode(ItemDrop d) => RuntimeHelpers.GetHashCode(d);
        }
    }
}
