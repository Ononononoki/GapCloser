using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // every lamp, torch and fire flickers every frame, also behind you. ValheimCommunityPatch already stops lights far away;
    // this pauses the flicker of a nearer one while its whole light (its range) lies outside the camera's view, and keeps
    // its clock going so a fade in stays right. such a light can only light what is outside the view, so nothing on screen
    // changes. lights that burn out on their own (a ttl) and sun-like lights are never paused
    internal static class LightFlickerOffscreen
    {
        private const string Name = "LightFlickerOffscreen";
        // added to a light's range, for the camera's move within a frame
        private const float Margin = 1f;
        private const float StatSeconds = 120f;

        private static readonly Plane[] Planes = new Plane[6];
        private static AccessTools.FieldRef<LightFlicker, float> _timeOf;
        private static AccessTools.FieldRef<LightFlicker, Light> _lightOf;
        private static int _planesFrame = -1;
        private static bool _havePlanes, _loggedFirst;
        private static int _paused, _updated;
        private static float _nextStat;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Sections.Fixes, Name, true,
                "Lamps, torches and fires whose light reaches nothing in view pause their flicker. Nothing on screen changes.");
            if (!enabled.Value || !Plugin.GameMatches(Name)) return;
            var update = AccessTools.Method(typeof(LightFlicker), "CustomUpdate", new[] { typeof(float), typeof(float) });
            if (update == null || AccessTools.Field(typeof(LightFlicker), "m_time") == null || AccessTools.Field(typeof(LightFlicker), "m_light") == null)
            {
                Plugin.Log.LogWarning($"{Name}: the game's light flicker update not found, the fix stays off");
                return;
            }
            _timeOf = AccessTools.FieldRefAccess<LightFlicker, float>("m_time");
            _lightOf = AccessTools.FieldRefAccess<LightFlicker, Light>("m_light");
            // after other mods' prefixes (ValheimCommunityPatch's distance pause)
            harmony.Patch(update, prefix: new HarmonyMethod(AccessTools.Method(typeof(LightFlickerOffscreen), nameof(Prefix))) { priority = Priority.Last });
            Plugin.Log.LogInfo($"{Name}: on, the flicker of a light whose whole range is out of view pauses");
        }

        // false skips the flicker for this frame, the light's clock still moves on
        private static bool Prefix(LightFlicker __instance, float __0)
        {
            if (__instance.m_ttl > 0f) return true;
            var light = _lightOf(__instance);
            if (!light || !light.enabled) return true;
            var type = light.type;
            if (type != LightType.Point && type != LightType.Spot) return true;
            int frame = Time.frameCount;
            if (frame != _planesFrame)
            {
                _planesFrame = frame;
                var camera = Utils.GetMainCamera();
                _havePlanes = camera != null;
                if (_havePlanes) GeometryUtility.CalculateFrustumPlanes(camera, Planes);
                Stat();
            }
            if (!_havePlanes || InView(__instance.transform.position, light.range + Margin))
            {
                _updated++;
                return true;
            }
            _timeOf(__instance) += __0;
            _paused++;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Plugin.Log.LogInfo($"{Name}: paused the flicker of a light out of view at {__instance.transform.position}, first time this session");
            }
            return false;
        }

        // the sphere touches the view: it is not wholly behind any of the six planes
        internal static bool InView(Vector3 center, float radius)
        {
            for (int i = 0; i < 6; i++)
                if (Planes[i].GetDistanceToPoint(center) < -radius) return false;
            return true;
        }

        private static void Stat()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextStat) return;
            if (_nextStat > 0f && _paused + _updated > 0)
                Plugin.Log.LogInfo($"{Name} {StatSeconds:0} s: paused {_paused} of {_paused + _updated} flicker updates ({100f * _paused / (_paused + _updated):0} %)");
            _nextStat = now + StatSeconds;
            _paused = _updated = 0;
        }
    }
}
