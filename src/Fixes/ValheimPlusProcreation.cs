using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // ValheimPlus 0.10.3.0 reads the creature of every breeding component after its Awake and throws when the component
    // has none. skipping its postfix in that case removes the error, it would have stopped at that same line anyway
    internal static class ValheimPlusProcreation
    {
        public const string TargetGuid = "org.bepinex.plugins.valheim_plus";
        private const string Name = "ValheimPlusProcreationError";
        private const string TargetType = "ValheimPlus.GameClasses.Procreation_Awake_Patch";
        private static readonly string[] CheckedVersions = { "0.10.3.0" };
        private static readonly AccessTools.FieldRef<Procreation, Character> CharacterOf = AccessTools.FieldRefAccess<Procreation, Character>("m_character");
        private static bool _actedOnce;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind("Fixes", Name, true,
                "ValheimPlus 0.10.3.0: stops the NullReferenceException of its breeding patch on objects without a creature. Works only with the checked versions of ValheimPlus.");
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
                harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ValheimPlusProcreation), nameof(Prefix))));
                Plugin.Log.LogInfo($"{Name}: on for ValheimPlus {string.Join(", ", CheckedVersions)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: could not patch {TargetType}.Postfix, the fix stays off: {e.Message}");
            }
        }

        // __0 is the breeding component, the target names it __instance, which harmony would read as this prefix's own
        private static bool Prefix(Procreation __0)
        {
            if (__0 != null && CharacterOf(__0) != null) return true;
            if (!_actedOnce)
            {
                _actedOnce = true;
                Plugin.Log.LogInfo($"{Name}: skipped ValheimPlus' breeding postfix on {(__0 != null ? __0.name : "a removed object")}, no creature, first time this session");
            }
            return false;
        }
    }
}
