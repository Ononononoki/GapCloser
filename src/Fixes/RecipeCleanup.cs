using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // console command gapcloser_fixrecipes: lists the recipes the local character knows but could never have learned by the game's
    // own rule (the recipe's station at its level and every material known), which the AzuCraftyBoxes bug taught at the
    // forge of potential. "gapcloser_fixrecipes apply" backs up the character file, removes them and saves the character.
    // the same cleanup also runs by itself when the character enters a world (RecipeCleanupOnSpawn)
    internal static class RecipeCleanup
    {
        internal enum OnSpawn { Off, WithAzuCraftyBoxes, Always }

        private static ConfigEntry<OnSpawn> _onSpawn;
        private static bool _azuCraftyBoxes;
        private static Game _doneFor;
        private const string Name = "RecipeCleanupCommand";
        private const string Command = "gapcloser_fixrecipes";
        private const int ShownInConsole = 40;
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownMaterial = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownMaterial");
        private static readonly AccessTools.FieldRef<Player, Dictionary<string, int>> KnownStations = AccessTools.FieldRefAccess<Player, Dictionary<string, int>>("m_knownStations");

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Sections.Fixes, Name, true,
                $"Adds the {Command} console command: lists the recipes a character could not have learned, {Command} apply backs up the character and removes them.");
            if (!enabled.Value || !Plugin.GameMatches(Name)) return;
            _onSpawn = config.Bind(Sections.Fixes, "RecipeCleanupOnSpawn", OnSpawn.WithAzuCraftyBoxes,
                $"Runs the {Command} cleanup by itself on entering a world: WithAzuCraftyBoxes (only with AzuCraftyBoxes installed), Always or Off. Backs up the character first.");
            _azuCraftyBoxes = Chainloader.PluginInfos.ContainsKey(AzuCraftyBoxesForgeRecipes.TargetGuid);
            harmony.Patch(AccessTools.Method(typeof(Terminal), "InitTerminal"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(RecipeCleanup), nameof(Register))));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnSpawned)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(RecipeCleanup), nameof(Spawned))));
            Plugin.Log.LogInfo($"{Name}: on, console command {Command}, on spawn: {_onSpawn.Value} (AzuCraftyBoxes installed: {_azuCraftyBoxes})");
        }

        private static void Register()
        {
            new Terminal.ConsoleCommand(Command, "lists recipes this character could not have learned (AzuCraftyBoxes forge bug), 'apply' removes them",
                (Terminal.ConsoleEvent)Run);
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null || ObjectDB.instance == null || Game.instance == null)
            {
                args.Context.AddString($"{Command}: load a character into a world first");
                return;
            }
            var wrong = Find(player);
            Plugin.Log.LogInfo($"{Command}: {wrong.Count} known recipes fail the game's learn rule: {string.Join(", ", wrong)}");
            if (wrong.Count == 0)
            {
                args.Context.AddString($"{Command}: nothing to remove, every known recipe passes the game's learn rule");
                return;
            }
            string mode = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "";
            if (mode != "apply" && mode != "applynobackup")
            {
                args.Context.AddString($"{Command}: {wrong.Count} recipes this character could not have learned (all of them in LogOutput.log):");
                args.Context.AddString(string.Join(", ", wrong.Take(ShownInConsole).Select(Localize)) + (wrong.Count > ShownInConsole ? ", ..." : ""));
                args.Context.AddString($"type '{Command} apply' to back up the character file and remove them");
                return;
            }
            string backup = mode == "apply" ? Backup() : "none (applynobackup)";
            if (backup == null)
            {
                args.Context.AddString($"{Command}: the character file could not be backed up, nothing removed (see LogOutput.log)");
                return;
            }
            var known = KnownRecipes(player);
            int removed = 0;
            foreach (string name in wrong)
                if (known.Remove(name)) removed++;
            Game.instance.SavePlayerProfile(false, false);
            args.Context.AddString($"{Command}: removed {removed} recipes and saved the character, backup: {backup}");
            Plugin.Log.LogInfo($"{Command}: removed {removed} recipes and saved the character, backup: {backup}");
        }

        // the first spawn into a world (not a respawn after death in the same world)
        private static void Spawned(Player __instance)
        {
            if (__instance == null || __instance != Player.m_localPlayer || Game.instance == null || ObjectDB.instance == null) return;
            var mode = _onSpawn.Value;
            if (mode == OnSpawn.Off || (mode == OnSpawn.WithAzuCraftyBoxes && !_azuCraftyBoxes) || _doneFor == Game.instance) return;
            _doneFor = Game.instance;
            var wrong = Find(__instance);
            if (wrong.Count == 0)
            {
                Plugin.Log.LogInfo($"{Command} on spawn: every known recipe passes the game's learn rule, nothing removed");
                return;
            }
            string backup = Backup();
            if (backup == null)
            {
                Plugin.Log.LogWarning($"{Command} on spawn: {wrong.Count} recipes fail the learn rule, but the character file could not be backed up, nothing removed");
                return;
            }
            var known = KnownRecipes(__instance);
            int removed = 0;
            foreach (string name in wrong)
                if (known.Remove(name)) removed++;
            Game.instance.SavePlayerProfile(false, false);
            Plugin.Log.LogInfo($"{Command} on spawn: removed {removed} recipes this character could not have learned ({string.Join(", ", wrong)}), backup: {backup}");
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, $"{Plugin.ModName}: removed {removed} recipes this character could not have learned");
        }

        // item names of known recipes that no recipe for that item could have taught by the game's own rule
        private static List<string> Find(Player player)
        {
            var known = KnownRecipes(player);
            var candidates = new HashSet<string>();
            var learnable = new HashSet<string>();
            foreach (var recipe in ObjectDB.instance.m_recipes)
            {
                string item = recipe?.m_item?.m_itemData?.m_shared?.m_name;
                if (item == null || !known.Contains(item)) continue;
                candidates.Add(item);
                if (Learnable(player, recipe)) learnable.Add(item);
            }
            candidates.ExceptWith(learnable);
            return candidates.OrderBy(n => n).ToList();
        }

        // the game's learn rule (Player.HaveRequirements with discover, as away from any station), without other mods' patches.
        // forge of potential idols never count against a recipe here, so a doubtful recipe is kept, never removed
        private static bool Learnable(Player player, Recipe recipe)
        {
            var station = recipe.m_craftingStation;
            if (station != null && (!KnownStations(player).TryGetValue(station.m_name, out int level) || level < recipe.m_minStationLevel)) return false;
            var materials = KnownMaterial(player);
            foreach (var requirement in recipe.m_resources)
            {
                if (requirement?.m_resItem == null || requirement.m_upgraderResource || requirement.m_amount <= 0) continue;
                bool known = materials.Contains(requirement.m_resItem.m_itemData.m_shared.m_name);
                if (recipe.m_requireOnlyOneIngredient)
                {
                    if (known) return true;
                }
                else if (!known) return false;
            }
            return !recipe.m_requireOnlyOneIngredient;
        }

        private static string Localize(string name) => Localization.instance != null ? Localization.instance.Localize(name) : name;

        // the game's own backup of a save (it writes "<name>_backup_<time>" next to it, also in the Steam Cloud)
        private static readonly MethodInfo CreateBackup = AccessTools.Method(typeof(SaveSystem), "CreateBackup");

        // backs up the character as it was last saved: a local file is copied into BepInEx/config/GapCloser_backups,
        // a Steam Cloud character (its path is a cloud path, no file on disk) gets the game's own backup next to it
        private static string Backup()
        {
            try
            {
                var profile = Game.instance.GetPlayerProfile();
                string source = profile.GetPath();
                if (!File.Exists(source) && File.Exists(source + ".fch")) source += ".fch";
                if (!File.Exists(source))
                {
                    if (CreateBackup != null && SaveSystem.TryGetSaveByName(profile.m_filename, SaveDataType.Character, out var save) && save?.PrimaryFile != null
                        && CreateBackup.Invoke(null, new object[] { save.PrimaryFile, DateTime.Now, profile.m_fileSource }) is bool made && made)
                        return $"the game's own backup of {profile.m_filename} ({profile.m_fileSource})";
                    Plugin.Log.LogWarning($"{Command}: character file not found at {source} and the game's own backup failed");
                    return null;
                }
                string folder = Path.Combine(BepInEx.Paths.ConfigPath, $"{Plugin.ModName}_backups");
                Directory.CreateDirectory(folder);
                string target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(source)}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(source)}");
                File.Copy(source, target);
                return target;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Command}: backup failed: {e.GetType().Name} {e.Message}");
                return null;
            }
        }
    }
}
