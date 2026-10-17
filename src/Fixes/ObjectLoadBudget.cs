using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace GapCloser.Fixes
{
    // the game creates the objects near the player up to max(10, waiting / 100) a frame (ZNetScene.CreateObjectsSorted)
    // and the far ones up to 10 (CreateDistantObjects), whatever each one costs: near a big base that is up to 100
    // heavy pieces in one frame. outside the loading screen this ends a frame's creating once it spent the budget (at
    // least a few objects a frame, nearest first as before), the rest come in the next frames. ValheimCommunityPatch
    // replaces the near loop with its own spawn queue, so the same rule goes into its loop too
    internal static class ObjectLoadBudget
    {
        private const string Name = "ObjectLoadBudget";
        private const int MinPerFrame = 2;
        internal const string QueueGuid = "MidnightsFX.ValheimCommunityPatch";
        private static readonly string[] QueueVersions = { "0.36.1", "0.37.0" };
        private const string QueueType = "ValheimCommunityPatch.Patches.Performance.SpawnQueueCachePatch";
        private const string QueueMethod = "CreateObjectsSortedPrefix";

        private static ConfigEntry<float> _budgetMs;
        private static Func<ZNetScene, bool> _inLoadingScreen;
        private static readonly Stopwatch Watch = new Stopwatch();
        private static bool _loading, _patched, _queuePatched, _loggedCut, _loggedQueueCut;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _budgetMs = config.Bind(Sections.Fixes, Name + "Ms", 4f,
                "Milliseconds a frame for making the objects near you outside loading screens, nearest first, so big bases load without frozen frames. 0 is the game's own way.");
            if (_budgetMs.Value <= 0f || !Plugin.GameMatches(Name)) return;
            var create = AccessTools.Method(typeof(ZNetScene), "CreateObjects");
            var sorted = AccessTools.Method(typeof(ZNetScene), "CreateObjectsSorted");
            var distant = AccessTools.Method(typeof(ZNetScene), "CreateDistantObjects");
            var loading = AccessTools.Method(typeof(ZNetScene), "InLoadingScreen");
            if (create == null || sorted == null || distant == null || loading == null)
            {
                Plugin.Log.LogWarning($"{Name}: the game's object creation did not look as checked, off");
                return;
            }
            _inLoadingScreen = AccessTools.MethodDelegate<Func<ZNetScene, bool>>(loading);
            _patched = true;
            harmony.Patch(sorted, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ObjectLoadBudget), nameof(SortedTranspiler))));
            harmony.Patch(distant, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ObjectLoadBudget), nameof(DistantTranspiler))));
            if (!_patched) return;
            harmony.Patch(create, prefix: new HarmonyMethod(AccessTools.Method(typeof(ObjectLoadBudget), nameof(Begin))));
            Plugin.Log.LogInfo($"{Name}: on, {_budgetMs.Value:0.#} ms a frame outside the loading screen");
            ApplyQueue(harmony);
        }

        // ValheimCommunityPatch's prefix on CreateObjectsSorted creates the near objects itself and skips the game's loop
        private static void ApplyQueue(Harmony harmony)
        {
            if (!Plugin.TargetMatches(Name + " spawn queue", QueueGuid, QueueVersions)) return;
            var queue = AccessTools.TypeByName(QueueType);
            var prefix = queue != null ? AccessTools.Method(queue, QueueMethod) : null;
            if (prefix == null)
            {
                Plugin.Log.LogWarning($"{Name}: ValheimCommunityPatch's spawn queue ({QueueType}.{QueueMethod}) not found, near objects there keep its own pace");
                return;
            }
            _queuePatched = true;
            harmony.Patch(prefix, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ObjectLoadBudget), nameof(QueueTranspiler))));
            if (_queuePatched) Plugin.Log.LogInfo($"{Name}: also on ValheimCommunityPatch's spawn queue (it replaces the game's near object loop)");
        }

        // each frame's creating starts here
        private static void Begin(ZNetScene __instance)
        {
            _loading = _inLoadingScreen(__instance);
            Watch.Restart();
        }

        // the near loop goes on while created < limit
        internal static int LimitBelow(int limit, int created) => Spent(created) ? created : limit;

        // the far loop goes on while created <= limit
        internal static int LimitAtMost(int limit, int created) => Spent(created) ? created - 1 : limit;

        // ValheimCommunityPatch's near loop goes on while created < limit, like the game's
        internal static int LimitBelowQueue(int limit, int created)
        {
            if (!Spent(created)) return limit;
            if (!_loggedQueueCut)
            {
                _loggedQueueCut = true;
                Plugin.Log.LogInfo($"{Name}: ValheimCommunityPatch's spawn queue stopped after {created} objects and {Watch.Elapsed.TotalMilliseconds:0.0} ms, the rest follow in the next frames, first time this session");
            }
            return created;
        }

        private static bool Spent(int created)
        {
            if (_loading || created < MinPerFrame || Watch.Elapsed.TotalMilliseconds < _budgetMs.Value) return false;
            if (!_loggedCut)
            {
                _loggedCut = true;
                Plugin.Log.LogInfo($"{Name}: a frame's object creating stopped after {created} objects and {Watch.Elapsed.TotalMilliseconds:0.0} ms, the rest follow in the next frames, first time this session");
            }
            return true;
        }

        // after "ldarg.3 ldind.i4 ldloc.1" (created, limit) of the near loop's check: limit = LimitBelow(limit, created)
        private static IEnumerable<CodeInstruction> SortedTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Insert(instructions, OpCodes.Ldloc_1, nameof(LimitBelow), 1, "CreateObjectsSorted", ref _patched);

        // after "ldarg.3 ldind.i4 ldarg.2" (created, max) of the far loop's two checks: max = LimitAtMost(max, created)
        private static IEnumerable<CodeInstruction> DistantTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Insert(instructions, OpCodes.Ldarg_2, nameof(LimitAtMost), 2, "CreateDistantObjects", ref _patched);

        // after "ldarg.3 ldind.i4 ldloc.s" (created, limit) of ValheimCommunityPatch's one check: limit = LimitBelowQueue(limit, created)
        private static IEnumerable<CodeInstruction> QueueTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Insert(instructions, OpCodes.Ldloc_S, nameof(LimitBelowQueue), 1, "ValheimCommunityPatch's spawn queue", ref _queuePatched);

        private static IEnumerable<CodeInstruction> Insert(IEnumerable<CodeInstruction> instructions, OpCode limitLoad, string helper, int expected, string where, ref bool patched)
        {
            var list = new List<CodeInstruction>(instructions);
            var sites = new List<int>();
            for (int i = 2; i < list.Count - 1; i++)
                if (list[i - 2].opcode == OpCodes.Ldarg_3 && list[i - 1].opcode == OpCodes.Ldind_I4 && list[i].opcode == limitLoad &&
                    (list[i + 1].opcode == OpCodes.Blt_S || list[i + 1].opcode == OpCodes.Blt || list[i + 1].opcode == OpCodes.Ble_S || list[i + 1].opcode == OpCodes.Ble))
                    sites.Add(i);
            if (sites.Count != expected)
            {
                Plugin.Log.LogWarning($"{Name}: {where} has {sites.Count} limit checks where {expected} were checked, left as it is");
                patched = false;
                return list;
            }
            var call = AccessTools.Method(typeof(ObjectLoadBudget), helper);
            for (int s = sites.Count - 1; s >= 0; s--)
                list.InsertRange(sites[s] + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_3),
                    new CodeInstruction(OpCodes.Ldind_I4),
                    new CodeInstruction(OpCodes.Call, call)
                });
            return list;
        }
    }
}
