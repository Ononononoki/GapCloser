using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // AzuCraftyBoxes 1.8.19 drops a recipe's normal materials at an upgrade station also when the game checks whether a
    // recipe can be learned, so the forge of potential teaches every recipe of every known station. this skips its check
    // in that one case and the game's own answer stands. crafting and upgrading from chests are not touched
    internal static class AzuCraftyBoxesForgeRecipes
    {
        public const string TargetGuid = "Azumatt.AzuCraftyBoxes";
        private const string Name = "AzuCraftyBoxesForgeRecipes";
        private const string TargetType = "AzuCraftyBoxes.Patches.PlayerHaveRequirementsPatchRBoolInt";
        private static readonly string[] CheckedVersions = { "1.8.19" };
        private static bool _actedOnce;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind("Fixes", Name, true,
                "AzuCraftyBoxes 1.8.19: stops the Forge of Potential from teaching every recipe. Works only with the checked versions of AzuCraftyBoxes.");
            if (!enabled.Value || !Plugin.TargetMatches(Name, TargetGuid, CheckedVersions)) return;
            var type = AccessTools.TypeByName(TargetType);
            var target = type == null ? null : AccessTools.Method(type, "Postfix");
            if (target == null)
            {
                Plugin.Log.LogWarning($"{Name}: {TargetType}.Postfix not found, the fix stays off");
                return;
            }
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(AzuCraftyBoxesForgeRecipes), nameof(Prefix))));
                Plugin.Log.LogInfo($"{Name}: on for AzuCraftyBoxes {string.Join(", ", CheckedVersions)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: could not patch {TargetType}.Postfix, the fix stays off: {e.Message}");
            }
        }

        // __0 is the player, the target names it __instance, which harmony would read as this prefix's own instance
        private static bool Prefix(Player __0, bool discover)
        {
            if (!discover || __0 == null) return true;
            var station = __0.GetCurrentCraftingStation();
            if (station == null || !station.m_upgrader) return true;
            if (!_actedOnce)
            {
                _actedOnce = true;
                Plugin.Log.LogInfo($"{Name}: skipped AzuCraftyBoxes' learn check at {station.m_name}, first time this session");
            }
            return false;
        }
    }
}
