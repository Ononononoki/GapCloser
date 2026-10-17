using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // ValheimPlus 0.10.3.0 counts a recipe's material in the chests in range by building a new list of every item in every
    // chest and counting that list with LINQ, for each shown material every frame the inventory is open, and again for
    // each material and quality level in every requirement check. this counts the same items straight from the same
    // chests with the same rules, so the amounts stay the same and no list is built
    internal static class ValheimPlusChestCounts
    {
        private const string Name = "ValheimPlusChestCounts";
        private const string PatchType = "ValheimPlus.GameClasses.InventoryGui_SetupRequirement_Patch";
        private const string CheckType = "ValheimPlus.GameClasses.Player_HaveRequirementItems_Transpiler";
        private const string AssistantType = "ValheimPlus.InventoryAssistant";
        private static readonly string[] CheckedVersions = { "0.10.3.0" };
        // stays empty, it only stands in for the list ValheimPlus would build
        private static readonly List<ItemDrop.ItemData> NoItems = new List<ItemDrop.ItemData>();
        private static System.Reflection.MethodInfo _listOf, _amountIn;
        private static List<Container> _chests;
        private static int _replaced, _site;
        private static bool _patchingCheck;
        private static readonly bool[] Logged = new bool[2];
        private static readonly string[] Sites = { "requirement display", "requirement check" };

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Sections.Fixes, Name, true,
                "ValheimPlus 0.10.3.0: counts chest materials for crafting from chests without garbage.");
            if (!enabled.Value || !Plugin.TargetMatches(Name, ValheimPlusProcreation.TargetGuid, CheckedVersions)) return;
            var patch = AccessTools.TypeByName(PatchType);
            var display = patch == null ? null : AccessTools.Method(patch, "Prefix");
            var check = AccessTools.TypeByName(CheckType);
            var quantity = check == null ? null : AccessTools.Method(check, "ComputeItemQuantity");
            var assistant = AccessTools.TypeByName(AssistantType);
            _listOf = assistant == null ? null : AccessTools.Method(assistant, "GetNearbyChestItemsByContainerList", new[] { typeof(List<Container>) });
            _amountIn = assistant == null ? null : AccessTools.Method(assistant, "GetItemAmountInItemList", new[] { typeof(List<ItemDrop.ItemData>), typeof(ItemDrop.ItemData), typeof(int), typeof(bool) });
            if (_listOf == null || _amountIn == null)
            {
                Plugin.Log.LogWarning($"{Name}: ValheimPlus' chest item helpers not found, the fix stays off");
                return;
            }
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ValheimPlusChestCounts), nameof(Transpiler)));
            var on = new List<string>();
            foreach (var (target, what) in new[] { (display, "requirement display"), (quantity, "requirement check") })
            {
                if (target == null)
                {
                    Plugin.Log.LogWarning($"{Name}: ValheimPlus' {what} not found, left as it is");
                    continue;
                }
                _replaced = 0;
                _patchingCheck = target == quantity;
                harmony.Patch(target, transpiler: transpiler);
                if (_replaced == 1) on.Add(what);
                else Plugin.Log.LogWarning($"{Name}: ValheimPlus' {what} did not look as checked, left as it is");
            }
            if (on.Count > 0) Plugin.Log.LogInfo($"{Name}: on for ValheimPlus 0.10.3.0 ({string.Join(", ", on)})");
        }

        // the list call and the count call that reads it become Hold and CountHeld, only when they come as a pair with
        // nothing but argument loads between them
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var lists = list.FindAll(i => i.Calls(_listOf));
            var counts = list.FindAll(i => i.Calls(_amountIn));
            if (lists.Count != 1 || counts.Count != 1) return list;
            int from = list.IndexOf(lists[0]), to = list.IndexOf(counts[0]);
            if (to <= from) return list;
            for (int i = from + 1; i < to; i++)
                if (list[i].operand is System.Reflection.MethodBase || list[i].Branches(out _) || list[i].labels.Count > 0) return list;
            list[from].operand = AccessTools.Method(typeof(ValheimPlusChestCounts), _patchingCheck ? nameof(HoldCheck) : nameof(Hold));
            list[to].operand = AccessTools.Method(typeof(ValheimPlusChestCounts), nameof(CountHeld));
            _replaced = 1;
            return list;
        }

        private static List<ItemDrop.ItemData> Hold(List<Container> chests)
        {
            _chests = chests;
            _site = 0;
            return NoItems;
        }

        private static List<ItemDrop.ItemData> HoldCheck(List<Container> chests)
        {
            _chests = chests;
            _site = 1;
            return NoItems;
        }

        // the same rules as ValheimPlus' GetItemAmountInItemList: same name, the quality when one is given, and with
        // matchWorldLevel only items of at least the world's level
        private static int CountHeld(List<ItemDrop.ItemData> unused, ItemDrop.ItemData item, int quality, bool matchWorldLevel)
        {
            var chests = _chests;
            _chests = null;
            if (chests == null) return 0;
            string name = item?.m_shared?.m_name;
            int sum = 0;
            foreach (var chest in chests)
            {
                var inventory = chest?.GetInventory();
                if (inventory == null) continue;
                foreach (var other in inventory.GetAllItems())
                {
                    if (other.m_shared.m_name != name) continue;
                    if (quality >= 0 && other.m_quality != quality) continue;
                    if (matchWorldLevel && other.m_worldLevel < Game.m_worldLevel) continue;
                    sum += other.m_stack;
                }
            }
            if (!Logged[_site])
            {
                Logged[_site] = true;
                Plugin.Log.LogInfo($"{Name}: {Sites[_site]} counted {name} in {chests.Count} chests without a list ({sum}), first time this session");
            }
            return sum;
        }
    }
}
