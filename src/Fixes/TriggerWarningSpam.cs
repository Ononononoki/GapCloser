using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // damage areas whose trigger damage is off (stakes at big bases) only write a warning on every trigger touch and
    // return. skipping the call when trigger damage is off removes the log spam and changes nothing else
    internal static class TriggerWarningSpam
    {
        private const string Name = "TriggerWarningSpam";
        // the code of Aoe.CauseTriggerDamage on Valheim 1.0.20 and 1.0.21 (CodeCheck)
        private static readonly string[] CheckedCode = { "8A724D24" };
        private static bool _actedOnce;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Sections.Fixes, Name, true,
                "Stops the warning spam from stakes and other damage areas.");
            if (!enabled.Value || !Plugin.GameMatches(Name)) return;
            var target = AccessTools.Method(typeof(Aoe), "CauseTriggerDamage");
            if (!Plugin.CodeMatches(Name, target, CheckedCode)) return;
            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(TriggerWarningSpam), nameof(Prefix))));
            Plugin.Log.LogInfo($"{Name}: on");
        }

        private static bool Prefix(Aoe __instance)
        {
            if (__instance.m_useTriggers) return true;
            if (!_actedOnce)
            {
                _actedOnce = true;
                Plugin.Log.LogInfo($"{Name}: skipped a warning-only trigger call on {__instance.name}, first time this session");
            }
            return false;
        }
    }
}
