using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // AzuCraftyBoxes 1.8.19 counts every material in every chest in range again for every recipe, every frame the inventory
    // is open, checks its pull rules for every chest each time (a new list per check), and builds a debug text for every
    // recipe you are short on even with its debug log off. this keeps each chest's count of a material until that chest's
    // inventory changes, keeps the pull rule answers for a few seconds, and skips the text unless the debug log is on.
    // nothing kept is used while crafting or building takes items
    internal static class AzuCraftyBoxesCraftingList
    {
        private const string Name = "AzuCraftyBoxesCraftingList";
        private const string ContainerType = "AzuCraftyBoxes.IContainers.VanillaContainer";
        private const string CheckType = "AzuCraftyBoxes.Patches.PlayerHaveRequirementsPatchRBoolInt";
        private const string BoxesType = "AzuCraftyBoxes.Util.Functions.Boxes";
        // kept answers are dropped at least this often, in case a chest changed without telling (a backstop)
        private const float CountSeconds = 2f;
        private const float RuleSeconds = 5f;
        private static readonly string[] CheckedVersions = { "1.8.19" };
        private static readonly Dictionary<(Inventory, string), int> Counts = new Dictionary<(Inventory, string), int>();
        private static readonly Dictionary<(string, string, string), bool> Rules = new Dictionary<(string, string, string), bool>();
        private static AccessTools.FieldRef<object, Container> _containerOf;
        private static ConfigEntryBase _debugLog;
        private static float _countsSince, _rulesSince;
        private static int _taking, _reused;
        private static bool _loggedReuse, _loggedText;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind("Fixes", Name, true,
                "Keeps each chest's count of a material until that chest changes and AzuCraftyBoxes' pull rule answers for a few seconds, and skips a debug text it builds for every recipe you are short on. Less work and garbage with the inventory or a crafting station open near many chests (AzuCraftyBoxes 1.8.19).");
            if (!enabled.Value || !Plugin.TargetMatches(Name, AzuCraftyBoxesForgeRecipes.TargetGuid, CheckedVersions)) return;
            var container = AccessTools.TypeByName(ContainerType);
            var itemCount = container == null ? null : AccessTools.Method(container, "ItemCount", new[] { typeof(string) });
            var containerField = container == null ? null : AccessTools.Field(container, "<_container>P");
            var check = AccessTools.TypeByName(CheckType);
            var haveItems = check == null ? null : AccessTools.Method(check, "HaveRequirementItems");
            var boxes = AccessTools.TypeByName(BoxesType);
            var canPull = boxes == null ? null : AccessTools.Method(boxes, "CanItemBePulled", new[] { typeof(string), typeof(string), typeof(string) });
            var plugin = AccessTools.TypeByName("AzuCraftyBoxes.AzuCraftyBoxesPlugin");
            _debugLog = plugin == null ? null : AccessTools.Field(plugin, "debugLogsEnabled")?.GetValue(null) as ConfigEntryBase;
            if (itemCount == null || containerField == null || containerField.FieldType != typeof(Container) || haveItems == null || canPull == null || _debugLog == null)
            {
                Plugin.Log.LogWarning($"{Name}: AzuCraftyBoxes' chest count, its chest, requirement check, pull rules or debug setting not found, the fix stays off");
                return;
            }
            _containerOf = AccessTools.FieldRefAccess<Container>(container, containerField.Name);
            harmony.Patch(itemCount, prefix: Method(nameof(CountPrefix)), postfix: Method(nameof(CountPostfix)));
            harmony.Patch(canPull, prefix: Method(nameof(RulePrefix)), postfix: Method(nameof(RulePostfix)));
            harmony.Patch(AccessTools.Method(typeof(Inventory), "Changed"), postfix: Method(nameof(Changed)));
            // nothing kept is used while items are taken for crafting or building
            var takeStart = new HarmonyMethod(AccessTools.Method(typeof(AzuCraftyBoxesCraftingList), nameof(TakeStart))) { priority = Priority.First };
            var takeEnd = Method(nameof(TakeEnd));
            harmony.Patch(AccessTools.Method(typeof(Player), "ConsumeResources"), prefix: takeStart, finalizer: takeEnd);
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "DoCrafting"), prefix: takeStart, finalizer: takeEnd);
            harmony.Patch(haveItems, transpiler: Method(nameof(Transpiler)));
            Plugin.Log.LogInfo($"{Name}: on for AzuCraftyBoxes 1.8.19");
        }

        private static HarmonyMethod Method(string name) => new HarmonyMethod(AccessTools.Method(typeof(AzuCraftyBoxesCraftingList), name));

        private static void TakeStart()
        {
            _taking++;
            Counts.Clear();
        }

        private static void TakeEnd()
        {
            if (--_taking < 0) _taking = 0;
            Counts.Clear();
        }

        private static void Changed()
        {
            if (Counts.Count > 0) Counts.Clear();
        }

        private static bool CountPrefix(object __instance, string __0, ref int __result, out (Inventory, string) __state)
        {
            __state = default;
            if (_taking > 0 || __0 == null) return true;
            var inventory = _containerOf(__instance)?.GetInventory();
            if (inventory == null) return true;
            float now = Time.realtimeSinceStartup;
            if (now - _countsSince > CountSeconds)
            {
                Counts.Clear();
                _countsSince = now;
            }
            if (Counts.TryGetValue((inventory, __0), out int count))
            {
                __result = count;
                if (++_reused == 1000 && !_loggedReuse)
                {
                    _loggedReuse = true;
                    Plugin.Log.LogInfo($"{Name}: reused kept chest counts 1000 times ({Counts.Count} kept now), first time this session");
                }
                return false;
            }
            __state = (inventory, __0);
            return true;
        }

        private static void CountPostfix(int __result, (Inventory, string) __state)
        {
            if (__state.Item1 != null && _taking == 0) Counts[__state] = __result;
        }

        private static bool RulePrefix(string __0, string __1, string __2, ref bool __result, out bool __state)
        {
            __state = false;
            float now = Time.realtimeSinceStartup;
            if (now - _rulesSince > RuleSeconds)
            {
                Rules.Clear();
                _rulesSince = now;
            }
            if (__0 != null && __1 != null && __2 != null && Rules.TryGetValue((__0, __1, __2), out bool pull))
            {
                __result = pull;
                return false;
            }
            __state = __0 != null && __1 != null && __2 != null;
            return true;
        }

        private static void RulePostfix(string __0, string __1, string __2, bool __result, bool __state)
        {
            if (__state) Rules[(__0, __1, __2)] = __result;
        }

        // the "Short on" debug text: string.Format right before AzuCraftyBoxes' debug-only log call becomes FormatIfDebug
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var format = AccessTools.Method(typeof(string), nameof(string.Format), new[] { typeof(string), typeof(object[]) });
            var ours = AccessTools.Method(typeof(AzuCraftyBoxesCraftingList), nameof(FormatIfDebug));
            var list = instructions.ToList();
            int replaced = 0;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (!list[i].Calls(format) || list[i + 1].opcode != OpCodes.Call || (list[i + 1].operand as MethodInfo)?.Name != "LogIfReleaseAndDebugEnable") continue;
                list[i].operand = ours;
                replaced++;
            }
            Plugin.Log.LogInfo($"{Name}: {replaced} debug text built only with AzuCraftyBoxes' debug log on");
            return list;
        }

        private static string FormatIfDebug(string format, object[] args)
        {
            if (string.Equals(_debugLog.BoxedValue?.ToString(), "On", StringComparison.Ordinal)) return string.Format(format, args);
            if (!_loggedText)
            {
                _loggedText = true;
                Plugin.Log.LogInfo($"{Name}: skipped a debug text while AzuCraftyBoxes' debug log is off, first time this session");
            }
            return string.Empty;
        }
    }
}
