using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // ExtraSlots 1.2.21's API.FindSlot builds the text "Custom" + id anew for every slot it checks, plus a closure and a
    // delegate, on every call. mods that look up their slots every frame (CosmeticArmor 12 times a frame with the
    // inventory open) make kilobytes of garbage a frame with it. this finds the same slot by the same two names in the
    // same order, with "Custom" + id built once per id
    internal static class ExtraSlotsFindSlot
    {
        public const string TargetGuid = "shudnal.ExtraSlots";
        private const string Name = "ExtraSlotsFindSlot";
        private static readonly string[] CheckedVersions = { "1.2.21" };
        private static readonly Dictionary<string, string> CustomIds = new Dictionary<string, string>();
        private static AccessTools.FieldRef<object, string> _idOf;
        private static FieldInfo _slots;
        private static System.Type _slotType;
        private static bool _logged;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Sections.Fixes, Name, true,
                "ExtraSlots 1.2.21: finds slots for other mods without garbage.");
            if (!enabled.Value || !Plugin.TargetMatches(Name, TargetGuid, CheckedVersions)) return;
            var api = AccessTools.TypeByName("ExtraSlots.API");
            var find = api == null ? null : AccessTools.Method(api, "FindSlot", new[] { typeof(string) });
            var slots = AccessTools.TypeByName("ExtraSlots.Slots");
            _slotType = AccessTools.TypeByName("ExtraSlots.Slots+Slot");
            _slots = slots == null ? null : AccessTools.Field(slots, "slots");
            var id = _slotType == null ? null : AccessTools.Field(_slotType, "_id");
            if (find == null || _slots == null || _slotType == null || id == null || _slots.FieldType != _slotType.MakeArrayType() || find.ReturnType != _slotType || id.FieldType != typeof(string))
            {
                Plugin.Log.LogWarning($"{Name}: ExtraSlots' FindSlot, its slot list or a slot's id not found as checked, the fix stays off");
                return;
            }
            _idOf = AccessTools.FieldRefAccess<string>(_slotType, "_id");
            harmony.Patch(find, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ExtraSlotsFindSlot), nameof(Transpiler))));
            Plugin.Log.LogInfo($"{Name}: on for ExtraSlots 1.2.21");
        }

        // the whole body becomes: return (Slot)Find(Slots.slots, slotID)
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Ldsfld, _slots);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ExtraSlotsFindSlot), nameof(Find)));
            yield return new CodeInstruction(OpCodes.Castclass, _slotType);
            yield return new CodeInstruction(OpCodes.Ret);
        }

        // the first slot whose id is the given id or "Custom" + the given id, like ExtraSlots' own check
        private static object Find(object[] slots, string id)
        {
            if (slots == null) return null;
            string custom = null;
            foreach (var slot in slots)
            {
                if (slot == null) continue;
                string own = _idOf(slot);
                if (own == id) return Found(slot);
                if (custom == null) custom = CustomId(id);
                if (own == custom) return Found(slot);
            }
            return null;
        }

        private static string CustomId(string id)
        {
            if (id == null) return "Custom";
            if (CustomIds.TryGetValue(id, out var custom)) return custom;
            custom = "Custom" + id;
            if (CustomIds.Count < 1024) CustomIds[id] = custom;
            return custom;
        }

        private static object Found(object slot)
        {
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"{Name}: found slot {_idOf(slot)} without building texts, first time this session");
            }
            return slot;
        }
    }
}
