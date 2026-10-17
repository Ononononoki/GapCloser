using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // ExtraSlots 1.2.21 adds a quiver count to every Inventory.CountItems for the BowsBeforeHoes mod, and makes a closure
    // object on every call before it checks whether that mod is there. with it absent the postfix does nothing else, so
    // it is skipped then
    internal static class ExtraSlotsCountItems
    {
        private const string Name = "ExtraSlotsCountItems";
        private const string PostfixType = "ExtraSlots.Compatibility.BBHCompat+Inventory_CountItems_BBHFix";
        private static readonly string[] CheckedVersions = { "1.2.21" };
        private static AccessTools.FieldRef<bool> _bbhOn;
        private static bool _logged;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind("Fixes", Name, true,
                "ExtraSlots 1.2.21: skips its BowsBeforeHoes quiver count on every item count while that mod is not installed (it only made garbage then).");
            if (!enabled.Value || !Plugin.TargetMatches(Name, ExtraSlotsFindSlot.TargetGuid, CheckedVersions)) return;
            var postfix = AccessTools.TypeByName(PostfixType);
            var target = postfix == null ? null : AccessTools.Method(postfix, "Postfix");
            var compat = AccessTools.TypeByName("ExtraSlots.Compatibility.BBHCompat");
            var on = compat == null ? null : AccessTools.Field(compat, "isEnabled");
            if (target == null || on == null || !on.IsStatic || on.FieldType != typeof(bool))
            {
                Plugin.Log.LogWarning($"{Name}: ExtraSlots' item count postfix or its BowsBeforeHoes switch not found, the fix stays off");
                return;
            }
            _bbhOn = AccessTools.StaticFieldRefAccess<bool>(on);
            harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ExtraSlotsCountItems), nameof(Prefix))));
            Plugin.Log.LogInfo($"{Name}: on for ExtraSlots 1.2.21 (BowsBeforeHoes support {(_bbhOn() ? "on, so the postfix still runs" : "off")})");
        }

        private static bool Prefix()
        {
            if (_bbhOn()) return true;
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"{Name}: skipped ExtraSlots' quiver count, BowsBeforeHoes is not there, first time this session");
            }
            return false;
        }
    }
}
