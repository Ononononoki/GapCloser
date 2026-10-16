using System;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using GapCloser.Fixes;
using UnityEngine;

namespace GapCloser
{
    // fixes for the game and for other mods, each one only for the versions it was checked against, and fast teleports
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency(AzuCraftyBoxesForgeRecipes.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ValheimPlusProcreation.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [DefaultExecutionOrder(30000)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "onononoki.GapCloser";
        public const string ModName = "GapCloser";
        public const string ModVersion = "1.1.0";

        // the game versions the fixes of the game's own code were checked against
        private static readonly string[] CheckedGameVersions = { "1.0.20" };

        internal static ManualLogSource Log;
        private static ConfigEntry<bool> _force;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _force = Config.Bind("General", "ForceOnUntestedVersions", false,
                "Turns the fixes on also for game and mod versions they were not checked against. A forced fix may do nothing or break something, the log names every forced fix.");
            _harmony = new Harmony(ModGuid);
            ApplyFix(nameof(AzuCraftyBoxesForgeRecipes), AzuCraftyBoxesForgeRecipes.Apply);
            ApplyFix(nameof(ValheimPlusProcreation), ValheimPlusProcreation.Apply);
            ApplyFix(nameof(TriggerWarningSpam), TriggerWarningSpam.Apply);
            ApplyFix(nameof(RecipeChecks), RecipeChecks.Apply);
            ApplyFix(nameof(RecipeCleanup), RecipeCleanup.Apply);
            ApplyFix(nameof(ErrorGuards), ErrorGuards.Apply);
            ApplyFix(nameof(FastTeleport), FastTeleport.Apply);
            ApplyFix(nameof(AzuCraftyBoxesCraftingList), AzuCraftyBoxesCraftingList.Apply);
            ApplyFix(nameof(PieceParticleCulling), PieceParticleCulling.Apply);
        }

        // a fix that can not be patched (possible when forced on an unchecked version) leaves the other fixes working
        private void ApplyFix(string name, Action<Harmony, ConfigFile> apply)
        {
            try
            {
                apply(_harmony, Config);
            }
            catch (Exception e)
            {
                Log.LogError($"{name}: could not be applied ({e.GetType().Name}: {e.Message}), the other fixes still work");
            }
        }

        // runs after every other script of the frame
        private void LateUpdate()
        {
            RecipeChecks.Flush();
            FastTeleport.Frame();
        }

        // true when the other mod is installed in a version the fix was checked against, otherwise logs why the fix stays off
        internal static bool TargetMatches(string fix, string guid, string[] versions)
        {
            if (!Chainloader.PluginInfos.TryGetValue(guid, out var info))
            {
                Log.LogInfo($"{fix}: {guid} is not installed, not needed");
                return false;
            }
            string installed = info.Metadata.Version.ToString();
            if (Array.IndexOf(versions, installed) < 0)
            {
                if (_force.Value)
                {
                    Log.LogWarning($"{fix}: {guid} {installed} is not a checked version ({string.Join(", ", versions)}), forced on by ForceOnUntestedVersions");
                    return true;
                }
                Log.LogWarning($"{fix}: {guid} {installed} is not a checked version ({string.Join(", ", versions)}), the fix stays off (ForceOnUntestedVersions turns it on)");
                return false;
            }
            return true;
        }

        // true when the game is a version the fix was checked against, otherwise logs why the fix stays off
        internal static bool GameMatches(string fix)
        {
            string installed = global::Version.CurrentVersion.ToString();
            if (Array.IndexOf(CheckedGameVersions, installed) >= 0) return true;
            if (_force.Value)
            {
                Log.LogWarning($"{fix}: Valheim {installed} is not a checked version ({string.Join(", ", CheckedGameVersions)}), forced on by ForceOnUntestedVersions");
                return true;
            }
            Log.LogWarning($"{fix}: Valheim {installed} is not a checked version ({string.Join(", ", CheckedGameVersions)}), the fix stays off (ForceOnUntestedVersions turns it on)");
            return false;
        }
    }
}
