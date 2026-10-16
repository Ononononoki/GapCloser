using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // about half the particle systems of building pieces (fires, ovens, portals) keep simulating when no camera sees them.
    // this sets them once, on the piece prefabs at start, to pause off screen and catch up when seen again, the mode the
    // game already uses for most of the others. only the effects change, lights are separate and not touched
    internal static class PieceParticleCulling
    {
        private const string Name = "PieceParticleCulling";

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind("Fixes", Name, true,
                "Fire, smoke and portal effects of building pieces pause while no camera sees them and catch up when seen again, like most of the game's other effects. Only the effects, lights are not touched. Works only with the checked game versions.");
            if (!enabled.Value || !Plugin.GameMatches(Name)) return;
            // after every other mod has added its prefabs to the scene's list
            harmony.Patch(AccessTools.Method(typeof(ZNetScene), "Awake"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PieceParticleCulling), nameof(SetPrefabs))) { priority = Priority.Last });
            Plugin.Log.LogInfo($"{Name}: on");
        }

        private static void SetPrefabs(ZNetScene __instance)
        {
            int systems = 0, pieces = 0;
            foreach (var prefab in __instance.m_prefabs)
            {
                if (prefab == null || prefab.GetComponent<Piece>() == null) continue;
                bool changed = false;
                foreach (var system in prefab.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = system.main;
                    if (main.cullingMode != ParticleSystemCullingMode.AlwaysSimulate) continue;
                    main.cullingMode = ParticleSystemCullingMode.PauseAndCatchup;
                    systems++;
                    changed = true;
                }
                if (changed) pieces++;
            }
            Plugin.Log.LogInfo($"{Name}: {systems} particle systems on {pieces} building pieces now pause off screen");
        }
    }
}
