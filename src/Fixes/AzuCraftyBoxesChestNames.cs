using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // AzuCraftyBoxes 1.8.19 asks every chest in range for its prefab name in each requirement check (for its pull rules),
    // and every answer reads the object's name (a new text) and cuts "(Clone)" off it (another one). with many chests that
    // is thousands of texts a second with the inventory or a station open. this keeps each chest's prefab name once read.
    // a removed chest is not answered from it, so the mod sees the same as before
    internal static class AzuCraftyBoxesChestNames
    {
        private const string Name = "AzuCraftyBoxesChestNames";
        private const string ContainerType = "AzuCraftyBoxes.IContainers.VanillaContainer";
        private const int MaxNames = 4096;
        private static readonly string[] CheckedVersions = { "1.8.19" };
        private static readonly Dictionary<Container, string> Names = new Dictionary<Container, string>(new SameObject());
        private static AccessTools.FieldRef<object, Container> _containerOf;
        private static bool _logged;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind("Fixes", Name, true,
                "AzuCraftyBoxes 1.8.19: keeps each chest's prefab name once read instead of building it anew for every chest in every requirement check. Same names, less garbage with the inventory or a station open near many chests.");
            if (!enabled.Value || !Plugin.TargetMatches(Name, AzuCraftyBoxesForgeRecipes.TargetGuid, CheckedVersions)) return;
            var container = AccessTools.TypeByName(ContainerType);
            var prefabName = container == null ? null : AccessTools.Method(container, "GetPrefabName", new System.Type[0]);
            var field = container == null ? null : AccessTools.Field(container, "<_container>P");
            if (prefabName == null || prefabName.ReturnType != typeof(string) || field == null || field.FieldType != typeof(Container))
            {
                Plugin.Log.LogWarning($"{Name}: AzuCraftyBoxes' chest prefab name or its chest not found, the fix stays off");
                return;
            }
            _containerOf = AccessTools.FieldRefAccess<Container>(container, field.Name);
            harmony.Patch(prefabName, prefix: new HarmonyMethod(AccessTools.Method(typeof(AzuCraftyBoxesChestNames), nameof(Prefix))));
            Plugin.Log.LogInfo($"{Name}: on for AzuCraftyBoxes 1.8.19");
        }

        // the same name AzuCraftyBoxes reads (Utils.GetPrefabName of the chest's object), kept per chest
        private static bool Prefix(object __instance, ref string __result)
        {
            var chest = _containerOf(__instance);
            if (chest == null) return true;
            if (!Names.TryGetValue(chest, out var name))
            {
                name = Utils.GetPrefabName(chest.gameObject);
                if (Names.Count >= MaxNames) Names.Clear();
                Names[chest] = name;
                if (!_logged)
                {
                    _logged = true;
                    Plugin.Log.LogInfo($"{Name}: kept the prefab name {name} of a chest, first time this session");
                }
            }
            __result = name;
            return false;
        }

        // a chest is the same chest only by reference (unity's own equality calls two removed objects equal)
        private sealed class SameObject : IEqualityComparer<Container>
        {
            public bool Equals(Container a, Container b) => ReferenceEquals(a, b);
            public int GetHashCode(Container c) => RuntimeHelpers.GetHashCode(c);
        }
    }
}
