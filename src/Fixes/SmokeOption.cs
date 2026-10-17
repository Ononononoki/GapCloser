using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    internal enum SmokeMode
    {
        Normal,
        Off
    }

    // a choice for the player, off by default: the game's smoke balls push each other and the roof every physics step,
    // the biggest physics cost near many fires. Off makes no smoke balls at all. the fires, kilns and smelters work as
    // before: only the ball is left out, the spawner keeps its own clock that tells them their smoke can rise. Off also
    // takes away the Smoked effect and smoke that smothers its own fire. changes apply at once, smoke already up fades
    internal static class SmokeOption
    {
        private const string Name = "Smoke";

        private static ConfigEntry<SmokeMode> _mode;
        private static bool _loggedSkip;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _mode = config.Bind(Sections.Options, Name, SmokeMode.Off,
                "Smoke of fires, kilns and smelters. Off (the default): no smoke, much less physics work near many fires, no Smoked debuff. Normal: the game's smoke.");
            if (!Plugin.GameMatches(Name)) return;
            var spawn = AccessTools.Method(typeof(SmokeSpawner), "Spawn", new[] { typeof(float) });
            if (spawn == null)
            {
                Plugin.Log.LogWarning($"{Name}: the game's smoke spawner not found, the game's smoke stays");
                return;
            }
            harmony.Patch(spawn, transpiler: new HarmonyMethod(AccessTools.Method(typeof(SmokeOption), nameof(Transpiler))));
            _mode.SettingChanged += (_, __) => Plugin.Log.LogInfo($"{Name}: {_mode.Value}");
            Plugin.Log.LogInfo($"{Name}: {_mode.Value}");
        }

        internal static SmokeMode Mode => _mode == null ? SmokeMode.Normal : _mode.Value;

        // the one Instantiate of the smoke ball in SmokeSpawner.Spawn becomes MakeBall, the rest of Spawn stays the game's
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var ours = AccessTools.Method(typeof(SmokeOption), nameof(MakeBall));
            int replaced = 0;
            foreach (var ins in list)
            {
                if (!(ins.operand is MethodInfo m) || (ins.opcode != OpCodes.Call && ins.opcode != OpCodes.Callvirt)) continue;
                if (m.Name != "Instantiate" || m.DeclaringType != typeof(UnityEngine.Object) || !m.IsGenericMethod) continue;
                var p = m.GetParameters();
                if (m.GetGenericArguments()[0] != typeof(GameObject) || p.Length != 3 || p[1].ParameterType != typeof(Vector3) || p[2].ParameterType != typeof(Quaternion)) continue;
                ins.opcode = OpCodes.Call;
                ins.operand = ours;
                replaced++;
            }
            if (replaced != 1) throw new InvalidOperationException($"{Name}: expected one smoke ball Instantiate in SmokeSpawner.Spawn, found {replaced}");
            return list;
        }

        private static GameObject MakeBall(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (Mode != SmokeMode.Off) return UnityEngine.Object.Instantiate(prefab, position, rotation);
            if (!_loggedSkip)
            {
                _loggedSkip = true;
                Plugin.Log.LogInfo($"{Name}: Off, a smoke ball was not made at {position}, first time this session");
            }
            return null;
        }
    }
}
