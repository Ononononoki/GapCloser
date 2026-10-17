using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // two patches of other mods that throw before they change anything. each guard skips the patch in exactly the case
    // where it would throw, so nothing else changes
    internal static class ErrorGuards
    {
        private static readonly AccessTools.FieldRef<Tameable, Character> TameCharacter = AccessTools.FieldRefAccess<Tameable, Character>("m_character");
        private static bool _fermenterOnce, _tameOnce;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            // AzuCraftyBoxes 1.8.19 reads the local player first thing in its fermenter postfix, ValheimPlus fills
            // fermenters from chests also while there is none (loading, dead), so it throws there
            Guard(harmony, config, "AzuCraftyBoxesFermenterError", AzuCraftyBoxesForgeRecipes.TargetGuid, new[] { "1.8.19" },
                "AzuCraftyBoxes.Patches.SearchContainersAsWell", nameof(Fermenter),
                "AzuCraftyBoxes 1.8.19: stops its fermenter error while there is no player.");
            // ValheimPlus 0.10.3.0 reads the creature of every tameable for its hover text, some objects have none
            Guard(harmony, config, "ValheimPlusTameHoverError", ValheimPlusProcreation.TargetGuid, new[] { "0.10.3.0" },
                "ValheimPlus.GameClasses.Tameable_GetHoverText_Patch", nameof(Tame),
                "ValheimPlus 0.10.3.0: stops its tame hover error on objects without a creature.");
        }

        private static void Guard(Harmony harmony, ConfigFile config, string name, string guid, string[] versions, string type, string prefix, string description)
        {
            var enabled = config.Bind(Sections.Fixes, name, true, description);
            if (!enabled.Value || !Plugin.TargetMatches(name, guid, versions)) return;
            var found = AccessTools.TypeByName(type);
            var target = found == null ? null : AccessTools.Method(found, "Postfix");
            if (target == null)
            {
                Plugin.Log.LogWarning($"{name}: {type}.Postfix not found, the fix stays off");
                return;
            }
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ErrorGuards), prefix)));
                Plugin.Log.LogInfo($"{name}: on for {guid} {string.Join(", ", versions)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{name}: could not patch {type}.Postfix, the fix stays off: {e.Message}");
            }
        }

        private static bool Fermenter()
        {
            if (Player.m_localPlayer != null) return true;
            if (!_fermenterOnce)
            {
                _fermenterOnce = true;
                Plugin.Log.LogInfo("AzuCraftyBoxesFermenterError: skipped AzuCraftyBoxes' fermenter postfix, no local player, first time this session");
            }
            return false;
        }

        // __0 is the tameable, the target names it __instance, which harmony would read as this prefix's own
        private static bool Tame(Tameable __0)
        {
            if (__0 != null && TameCharacter(__0) != null) return true;
            if (!_tameOnce)
            {
                _tameOnce = true;
                Plugin.Log.LogInfo($"ValheimPlusTameHoverError: skipped ValheimPlus' tame hover text on {(__0 != null ? __0.name : "a removed object")}, no creature, first time this session");
            }
            return false;
        }
    }
}
