using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // the game holds every portal trip at least 8 seconds and every other teleport 2, even when the destination is loaded
    // already. this fades to black in FastTeleportFadeSeconds, moves the player as soon as the screen is black, and lets
    // the game end the trip as soon as the destination is ready: the area loaded, the floor found and, with
    // ValheimCommunityPatch, the server's buildings there. the game's own 15 second fallback stays
    internal static class FastTeleport
    {
        private const string Name = "FastTeleport";
        // the game moves the player once its timer passes 2 and lets a portal trip end once it passes 8
        private const float PastGameWaits = 8.01f;
        // the player counts as moved once this close to the target on the ground plane
        private const float MovedWithin = 1f;
        private static readonly AccessTools.FieldRef<Player, float> Timer = AccessTools.FieldRefAccess<Player, float>("m_teleportTimer");
        private static readonly AccessTools.FieldRef<Player, Vector3> FromPos = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportFromPos");
        private static readonly AccessTools.FieldRef<Player, Vector3> TargetPos = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportTargetPos");
        private static readonly AccessTools.FieldRef<Player, bool> Distant = AccessTools.FieldRefAccess<Player, bool>("m_distantTeleport");
        private static ConfigEntry<float> _fade;
        private static ConfigEntry<bool> _log;

        // one trip of the local player, times in seconds from its start (-1 until reached)
        private class Trip
        {
            public float Start, Black = -1f, Moved = -1f, Ready = -1f;
            public Vector3 From, To;
            public bool Portal, LoadedAtStart;
        }

        private static Trip _trip;

        // the frames after a trip ends, measured for the teleport log
        private const float AfterSeconds = 5f;
        private const float SlowFrameMs = 40f;
        private static float _afterUntil = -1f, _afterWorst;
        private static int _afterSlow, _afterFrames;
        private static double _afterSum;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind("Fixes", Name, true,
                "Teleports and portals take only as long as loading the destination really needs: a short fade to black, the move at once, and back as soon as the area, its floor and (with ValheimCommunityPatch) the server's buildings are there. Works only with the checked game versions.");
            _fade = config.Bind("Fixes", "FastTeleportFadeSeconds", 0.25f, new ConfigDescription(
                "Seconds the screen takes to fade to black, and back, on a teleport (the game uses 1).", new AcceptableValueRange<float>(0.05f, 1f)));
            _log = config.Bind("Debug", "LogTeleports", false,
                "Writes one line per teleport to the log: distance, whether the destination was loaded already, and when the screen was black, the player moved, the area was ready and the trip ended.");
            if (!enabled.Value || !Plugin.GameMatches(Name)) return;
            var update = AccessTools.Method(typeof(Player), "UpdateTeleport");
            harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(FastTeleport), nameof(BeforeUpdate))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(FastTeleport), nameof(AfterUpdate))));
            harmony.Patch(AccessTools.Method(typeof(Hud), "GetFadeDuration"), postfix: new HarmonyMethod(AccessTools.Method(typeof(FastTeleport), nameof(Fade))));
            Plugin.Log.LogInfo($"{Name}: on, fade {_fade.Value} s, log {_log.Value}");
        }

        private static void BeforeUpdate(Player __instance)
        {
            if (__instance != Player.m_localPlayer || !__instance.IsTeleporting()) return;
            if (_trip == null)
            {
                var to = TargetPos(__instance);
                _trip = new Trip
                {
                    Start = Time.time, From = FromPos(__instance), To = to, Portal = Distant(__instance),
                    LoadedAtStart = ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(to),
                };
            }
            var hud = Hud.instance;
            if (hud == null || hud.m_loadingScreen == null || hud.m_loadingScreen.alpha < 0.99f || Timer(__instance) >= PastGameWaits) return;
            // the screen is black: skip the game's fixed waits, its own readiness checks still decide when the trip ends
            Timer(__instance) = PastGameWaits;
            _trip.Black = Time.time - _trip.Start;
        }

        private static void AfterUpdate(Player __instance)
        {
            if (__instance != Player.m_localPlayer || _trip == null) return;
            float now = Time.time - _trip.Start;
            // also on the trip's last frame, where the game often moves the player and ends the trip in one call
            var at = __instance.transform.position;
            if (_trip.Moved < 0f && new Vector2(at.x - _trip.To.x, at.z - _trip.To.z).magnitude < MovedWithin) _trip.Moved = now;
            if (_trip.Moved >= 0f && _trip.Ready < 0f && ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(_trip.To)) _trip.Ready = now;
            if (__instance.IsTeleporting()) return;
            var trip = _trip;
            _trip = null;
            if (!_log.Value) return;
            Plugin.Log.LogInfo($"{Name}: {(trip.Portal ? "portal" : "teleport")} of {Vector3.Distance(trip.From, trip.To):0} m, destination {(trip.LoadedAtStart ? "loaded already" : "not loaded")}: " +
                $"black {Seconds(trip.Black)}, moved {Seconds(trip.Moved)}, area ready {Seconds(trip.Ready)}, done {now:0.00} s{(now >= 15f ? " (the game's 15 s fallback)" : "")}");
            _afterUntil = Time.unscaledTime + AfterSeconds;
            _afterSlow = _afterFrames = 0;
            _afterWorst = 0f;
            _afterSum = 0.0;
        }

        // from the plugin's LateUpdate: the frames right after a trip, for the teleport log
        internal static void Frame()
        {
            if (_afterUntil < 0f) return;
            float ms = Time.unscaledDeltaTime * 1000f;
            _afterFrames++;
            _afterSum += ms;
            if (ms > SlowFrameMs) _afterSlow++;
            if (ms > _afterWorst) _afterWorst = ms;
            if (Time.unscaledTime < _afterUntil) return;
            _afterUntil = -1f;
            Plugin.Log.LogInfo($"{Name}: the {AfterSeconds:0} s after arrival: {_afterSlow} frames over {SlowFrameMs:0} ms, worst {_afterWorst:0} ms, average {_afterSum / _afterFrames:0.0} ms");
        }

        private static string Seconds(float value) => value < 0f ? "never" : $"{value:0.00} s";

        private static void Fade(Player player, ref float __result)
        {
            if (player != null && player.IsTeleporting() && !player.IsDead() && !player.IsSleeping())
                __result = _fade.Value;
        }
    }
}
