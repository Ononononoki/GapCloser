using System;
using System.Collections.Generic;
using System.Linq;
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
    [BepInDependency(ExtraSlotsFindSlot.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ObjectLoadBudget.QueueGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [DefaultExecutionOrder(30000)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "onononoki.GapCloser";
        public const string ModName = "GapCloser";
        public const string ModVersion = "1.3.2";

        // the game versions the fixes of the game's own code were checked against
        private static readonly string[] CheckedGameVersions = { "1.0.20" };

        internal static ManualLogSource Log;
        private static ConfigEntry<bool> _force;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            MoveOldSettings();
            _force = Config.Bind(Sections.General, "ForceOnUntestedVersions", false,
                "Turns the fixes on also for game and mod versions they were not checked against. At your own risk, the log names each one.");
            _harmony = new Harmony(ModGuid);
            ApplyFix(nameof(AzuCraftyBoxesForgeRecipes), AzuCraftyBoxesForgeRecipes.Apply);
            ApplyFix(nameof(ValheimPlusProcreation), ValheimPlusProcreation.Apply);
            ApplyFix(nameof(TriggerWarningSpam), TriggerWarningSpam.Apply);
            ApplyFix(nameof(RecipeChecks), RecipeChecks.Apply);
            ApplyFix(nameof(RecipeCleanup), RecipeCleanup.Apply);
            ApplyFix(nameof(ErrorGuards), ErrorGuards.Apply);
            ApplyFix(nameof(AzuCraftyBoxesCraftingList), AzuCraftyBoxesCraftingList.Apply);
            ApplyFix(nameof(PieceParticleCulling), PieceParticleCulling.Apply);
            ApplyFix(nameof(ValheimPlusChestCounts), ValheimPlusChestCounts.Apply);
            ApplyFix(nameof(ExtraSlotsFindSlot), ExtraSlotsFindSlot.Apply);
            ApplyFix(nameof(ExtraSlotsCountItems), ExtraSlotsCountItems.Apply);
            ApplyFix(nameof(AzuCraftyBoxesChestNames), AzuCraftyBoxesChestNames.Apply);
            // the sun shadow setting is bound before occlusion, which hands it its shaders
            ApplyFix(nameof(SunShadows), SunShadows.Apply);
            ApplyFix(nameof(OcclusionCulling), OcclusionCulling.Apply);
            ApplyFix(nameof(RockSupport), RockSupport.Apply);
            ApplyFix(nameof(ObjectLoadBudget), ObjectLoadBudget.Apply);
            ApplyFix(nameof(HiddenLights), HiddenLights.Apply);
            ApplyFix(nameof(PieceBatching), PieceBatching.Apply);
            ApplyFix(nameof(ChestReloadSkip), ChestReloadSkip.Apply);
            ApplyFix(nameof(SmokeOption), SmokeOption.Apply);
            ApplyFix(nameof(LightFlickerOffscreen), LightFlickerOffscreen.Apply);
            ApplyFix(nameof(AzuCraftyBoxesBuildCounts), AzuCraftyBoxesBuildCounts.Apply);
            ApplyFix(nameof(BenchmarkAll), BenchmarkAll.Apply);
        }

        // plugin identities are available after the loader finishes creating the plugins
        private void Start()
        {
            ApplyFix(nameof(FastTeleport), FastTeleport.Apply);
        }

        // the sections before 1.3.0, and where their settings are now
        private static readonly Dictionary<string, string> OldSections = new Dictionary<string, string>
        {
            { "General", Sections.General }, { "Options", Sections.Options }, { "Fixes", Sections.Fixes },
            { "RockSupport", Sections.RockSupport }, { "Debug", Sections.Debug }
        };

        // test and log settings that moved to the debug section, the rock ones with a clearer name
        private static readonly Dictionary<string, string> ToDebug = new Dictionary<string, string>
        {
            { "Fixes.OcclusionCullingKey", "OcclusionCullingKey" },
            { "Fixes.OcclusionCullingBenchmarkKey", "OcclusionCullingBenchmarkKey" },
            { "Fixes.OcclusionCullingLogLines", "OcclusionCullingLogLines" },
            { "Fixes.BenchmarkKey", "BenchmarkKey" },
            { "Fixes.ChestReloadSkipSelfCheck", "ChestReloadSkipSelfCheck" },
            { "Fixes.AzuCraftyBoxesBuildCountsSelfCheck", "AzuCraftyBoxesBuildCountsSelfCheck" },
            { "RockSupport.SelfCheck", "RockSupportSelfCheck" },
            { "RockSupport.LogChecks", "RockSupportLogChecks" }
        };

        // settings of test tools that are not in GapCloser any more
        private static readonly HashSet<string> Dropped = new HashSet<string>
        {
            "Fixes.SmoothPacing", "Fixes.SmoothPacingKey", "Fixes.OpenSkySmoke", "Fixes.ShadowBenchmarkKey"
        };

        // before anything is bound: a value set in an older cfg moves with its setting to the new sections. it runs once,
        // the old lines are gone from the file with the first save
        private void MoveOldSettings()
        {
            var orphans = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(Config, null) as Dictionary<ConfigDefinition, string>;
            if (orphans == null)
            {
                Log.LogWarning("the settings of an older version could not be read, they are back at their defaults");
                return;
            }
            int moved = 0, dropped = 0;
            foreach (var old in orphans.Keys.ToList())
            {
                if (!OldSections.TryGetValue(old.Section, out var section)) continue;
                string id = old.Section + "." + old.Key;
                string value = orphans[old];
                orphans.Remove(old);
                if (Dropped.Contains(id))
                {
                    dropped++;
                    continue;
                }
                var now = ToDebug.TryGetValue(id, out var key) ? new ConfigDefinition(Sections.Debug, key) : new ConfigDefinition(section, old.Key);
                if (!orphans.ContainsKey(now)) orphans[now] = value;
                moved++;
            }
            if (moved + dropped > 0) Log.LogInfo($"settings moved to the new sections: {moved} with their values, {dropped} of removed test tools dropped");
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
            Benchmark.Frame();
            PieceBatching.Frame();
            OcclusionCulling.Frame();
            HiddenLights.Frame();
            RockSupport.Frame();
            ChestReloadSkip.Frame();
            BenchmarkAll.Frame();
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

        // ForceOnUntestedVersions, for a fix with a check of its own (the graphics API)
        internal static bool ForceOnUntested => _force.Value;

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
