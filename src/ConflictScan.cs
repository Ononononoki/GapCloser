using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace GapCloser
{
    // at the main menu (every mod loaded and patched) and again once the player spawned: names the other mods that patch
    // the same methods as GapCloser. it only writes the log, no fix turns off because of it. lines come for a prefix that
    // can skip code a GapCloser fix edits, for another edit of the same code, and for the mods checked by hand
    internal static class ConflictScan
    {
        private const string Name = "ConflictScan";
        // 0 waits for the main menu, 1 for the player, 2 is done
        private static int _stage;
        private static readonly HashSet<string> Seen = new HashSet<string>();

        // other mods checked by hand: part of its assembly name, the patched method, the kind of patch, what it means
        private static readonly (string mod, string method, string kind, string text)[] Known =
        {
            ("ValheimCommunityPatch", "ZNetScene.CreateObjectsSorted", "prefix", "its spawn queue replaces this loop, ObjectLoadBudget's own line says whether its budget is inside the queue"),
            ("ValheimCommunityPatch", "ZNetScene.CreateObjectsSorted", "transpiler", "it swaps the missing prefab log line, another place than ObjectLoadBudget's, no conflict"),
            ("ValheimCommunityPatch", "ZNetScene.CreateDistantObjects", "transpiler", "it swaps the missing prefab log line, another place than ObjectLoadBudget's, no conflict"),
            ("ValheimOptimized", "ZNetScene.CreateObjectsSorted", "prefix", "it runs its own budgeted loop in game, so ObjectLoadBudget only works on the loading screen there (no error)"),
            ("ValheimOptimized", "ZNetScene.CreateDistantObjects", "prefix", "it runs its own budgeted loop when over its budget, ObjectLoadBudget only works where it lets the game's loop run (no error)"),
            ("BalrondCoreOptimizer", "LightFlicker.CustomUpdate", "prefix", "both only skip flicker work of distant or unseen lights, no conflict"),
        };

        internal static void Frame()
        {
            if (_stage == 2) return;
            try
            {
                if (_stage == 0 && FejdStartup.instance != null)
                {
                    _stage = 1;
                    Run("at the main menu");
                }
                else if (_stage == 1 && Player.m_localPlayer != null)
                {
                    _stage = 2;
                    Run("after spawning");
                }
            }
            catch (Exception e)
            {
                _stage = 2;
                Plugin.Log.LogWarning($"{Name}: could not read the patches ({e.GetType().Name}: {e.Message}), nothing changed");
            }
        }

        private static void Run(string when)
        {
            int ours = 0, shared = 0, flagged = 0, lines = 0;
            foreach (var method in Harmony.GetAllPatchedMethods().ToList())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                var mine = All(info).Where(p => p.owner == Plugin.ModGuid).ToList();
                if (mine.Count == 0) continue;
                ours++;
                var others = All(info).Where(p => p.owner != Plugin.ModGuid).ToList();
                if (others.Count == 0) continue;
                shared++;
                string target = $"{method.DeclaringType?.Name}.{method.Name}";
                string fixes = string.Join(", ", mine.Select(p => p.PatchMethod.DeclaringType?.Name).Distinct());
                bool weEdit = info.Transpilers.Any(p => p.owner == Plugin.ModGuid);
                foreach (var p in others)
                {
                    string mod = ModName(p);
                    string assembly = p.PatchMethod.DeclaringType?.Assembly.GetName().Name ?? "";
                    string kind = info.Prefixes.Contains(p) ? "prefix" : info.Transpilers.Contains(p) ? "transpiler" : null;
                    if (kind == null || !Seen.Add($"{target}|{p.owner}|{p.PatchMethod.Name}|{kind}")) continue;
                    var known = Known.FirstOrDefault(k => assembly.IndexOf(k.mod, StringComparison.OrdinalIgnoreCase) >= 0 && k.method == target && k.kind == kind);
                    if (known.text != null)
                    {
                        Plugin.Log.LogInfo($"{Name}: {mod} {kind} on {target} ({fixes}): {known.text}");
                        lines++;
                    }
                    else if (weEdit && kind == "prefix" && (p.PatchMethod as MethodInfo)?.ReturnType == typeof(bool))
                    {
                        Plugin.Log.LogWarning($"{Name}: {mod} has a prefix on {target} that can skip it, {fixes} edits inside it and does not run while it is skipped");
                        flagged++;
                        lines++;
                    }
                    else if (weEdit && kind == "transpiler")
                    {
                        Plugin.Log.LogInfo($"{Name}: {mod} also edits {target}, {fixes} edits it too (its own line says whether it found its places)");
                        flagged++;
                        lines++;
                    }
                }
            }
            Plugin.Log.LogInfo($"{Name} ({when}): GapCloser patches {ours} methods, other mods patch {shared} of them too, " +
                $"{flagged} can skip or also edit code GapCloser edits{(lines > 0 ? ", see the lines above" : "")}");
        }

        private static IEnumerable<Patch> All(Patches info) => info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers);

        // the plugin's name for its harmony id, else the assembly of its patch
        private static string ModName(Patch p)
        {
            if (Chainloader.PluginInfos.TryGetValue(p.owner, out var plugin)) return plugin.Metadata.Name;
            return p.PatchMethod.DeclaringType?.Assembly.GetName().Name ?? p.owner;
        }
    }
}
