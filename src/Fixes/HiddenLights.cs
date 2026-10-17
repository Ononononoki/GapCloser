using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace GapCloser.Fixes
{
    // newer, still being tested, with OcclusionCulling On: the lights of building pieces (fires, torches, lamps) whose whole
    // light range and flames lie behind what the camera's depth image shows go dark, their flicker script and flames pause,
    // and all of it comes back at the first visible result. a light lights nothing visible when every visible surface lies
    // in front of its range. glass and other see-through materials write no depth, so a light behind glass stays on.
    // the idea of turning off fires nobody can see comes from ValheimBuildOptimization by RockBreaker (Dracoalias)
    internal static class HiddenLights
    {
        private const string Name = "HiddenLights";
        private const float Margin = 0.5f;
        private const float MarginScale = 0.02f;
        private const int HideAfter = 3;
        private const int MaxResultAge = 8;
        private const float StaleMeters = 0.75f;
        private const float StaleDegrees = 8f;
        private const float ListSeconds = 1f;

        private static ConfigEntry<bool> _enabled;
        private static readonly AccessTools.FieldRef<LightLod, float> BaseRange = AccessTools.FieldRefAccess<LightLod, float>("m_baseRange");

        // the lights, flicker scripts and flames of one building piece, and what was changed while hidden
        private sealed class Entry
        {
            public Piece Piece;
            public Light[] Lights;
            public LightFlicker[] Flickers;
            public ParticleSystem[] Systems;
            public ParticleSystemRenderer[] Flames;
            public Bounds Bounds;
            public bool Hidden;
            public int Streak, SeenList;
            public readonly List<Light> OffLights = new List<Light>();
            public readonly List<LightFlicker> OffFlickers = new List<LightFlicker>();
            public readonly List<ParticleSystem> Paused = new List<ParticleSystem>();
            public readonly List<ParticleSystemRenderer> OffFlames = new List<ParticleSystemRenderer>();
        }

        private static readonly Dictionary<Piece, Entry> Cache = new Dictionary<Piece, Entry>(new SameObject());
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<Vector4> BoundsData = new List<Vector4>();
        private static readonly List<Piece> Stale = new List<Piece>();
        private static ComputeBuffer _bounds, _result, _rect;
        private static int _count, _listVersion, _sentList, _sentFrame;
        private static float _nextList;
        private static bool _pending, _loggedStart, _loggedHide;
        private static Vector3 _sentPos;
        private static Quaternion _sentRot;

        // the pieces whose lights are dark right now (the probe reads it for its per frame csv)
        public static int HiddenCount;

        // the run of all benchmarks switches this part on its own
        private static bool _partOn = true;
        internal static bool PartOn => _partOn;
        internal static bool PartAvailable => _enabled != null && _enabled.Value && OcclusionCulling.CameraPartAvailable;

        internal static void SetPart(bool on)
        {
            _partOn = on;
            if (!on && HiddenCount > 0) ShowAll();
        }

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _enabled = config.Bind(Sections.Fixes, Name, true,
                "With OcclusionCulling On: lights of buildings whose whole light the walls hide go dark until they can be seen.");
            if (!_enabled.Value) return;
            Plugin.Log.LogInfo($"{Name}: on, works while OcclusionCulling hides (margin {Margin} m plus {MarginScale:P0} of the distance, after {HideAfter} hidden results in a row)");
        }

        // once a frame from the plugin: the candidate list once a second, hidden lights held off (the game's light lod turns
        // a light in range back on by itself), everything back when occlusion stops hiding
        internal static void Frame()
        {
            if (_enabled == null || !_enabled.Value) return;
            if (!OcclusionCulling.HidingNow || !_partOn)
            {
                if (HiddenCount > 0) ShowAll();
                return;
            }
            foreach (var entry in Entries)
                if (entry.Hidden)
                    foreach (var light in entry.OffLights)
                        if (light != null && light.enabled) light.enabled = false;
            if (Time.time < _nextList) return;
            _nextList = Time.time + ListSeconds;
            RebuildList();
        }

        private static void RebuildList()
        {
            _listVersion++;
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type != LightType.Point && light.type != LightType.Spot) continue;
                var piece = light.GetComponentInParent<Piece>();
                if (piece == null || Cache.ContainsKey(piece)) continue;
                var entry = Read(piece);
                if (entry != null) Cache[piece] = entry;
            }
            Entries.Clear();
            BoundsData.Clear();
            Stale.Clear();
            foreach (var pair in Cache)
            {
                var entry = pair.Value;
                if (pair.Key == null)
                {
                    Stale.Add(pair.Key);
                    continue;
                }
                entry.SeenList = _listVersion;
                Entries.Add(entry);
                BoundsData.Add(entry.Bounds.center);
                BoundsData.Add(entry.Bounds.extents);
            }
            // pieces gone: put back what is still there, forget them
            foreach (var piece in Stale)
            {
                if (Cache.TryGetValue(piece, out var entry) && entry.Hidden) Show(entry);
                Cache.Remove(piece);
            }
            _count = Entries.Count;
            if (_count == 0) return;
            if (_bounds == null || _bounds.count < BoundsData.Count)
            {
                _bounds?.Release();
                _result?.Release();
                _rect?.Release();
                int capacity = Math.Max(256, _count * 3 / 2);
                _bounds = new ComputeBuffer(capacity * 2, 16);
                _result = new ComputeBuffer(capacity, 16);
                _rect = new ComputeBuffer(capacity, 16);
            }
            _bounds.SetData(BoundsData);
        }

        // a still building piece with lights: left out are pieces under a rigidbody (ships, carts) and without a valid network
        // object (the placement ghost). its box holds every light's full range and every flame
        private static Entry Read(Piece piece)
        {
            var view = piece.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || piece.GetComponentInParent<Rigidbody>() != null) return null;
            var lights = new List<Light>();
            Bounds bounds = default;
            bool any = false;
            foreach (var light in piece.GetComponentsInChildren<Light>(true))
            {
                if (light.type != LightType.Point && light.type != LightType.Spot) continue;
                lights.Add(light);
                var lod = light.GetComponent<LightLod>();
                float range = Mathf.Max(light.range, lod != null ? BaseRange(lod) : 0f);
                var box = new Bounds(light.transform.position, Vector3.one * (2f * range));
                if (!any) bounds = box;
                else bounds.Encapsulate(box);
                any = true;
            }
            if (!any) return null;
            var systems = piece.GetComponentsInChildren<ParticleSystem>(true);
            var flames = piece.GetComponentsInChildren<ParticleSystemRenderer>(true);
            foreach (var flame in flames)
                if (flame.enabled && flame.gameObject.activeInHierarchy) bounds.Encapsulate(flame.bounds);
            return new Entry
            {
                Piece = piece,
                Lights = lights.ToArray(),
                Flickers = piece.GetComponentsInChildren<LightFlicker>(true),
                Systems = systems,
                Flames = flames,
                Bounds = bounds,
            };
        }

        // from OcclusionCulling's dispatch, after its pyramid: the same test on the light boxes, read back in the same command buffer
        internal static void Record(CommandBuffer cmd, OcclusionGpu gpu, Camera cam, Matrix4x4 worldToClip, Matrix4x4 worldToCamera, float inflate)
        {
            if (_enabled == null || !_enabled.Value || !_partOn || _count == 0 || _pending || _bounds == null) return;
            gpu.RecordTest(cmd, worldToClip, worldToCamera, _bounds, _count, cam.nearClipPlane, Margin, MarginScale, inflate, 0, _result, _rect);
            _pending = true;
            _sentList = _listVersion;
            _sentFrame = Time.frameCount;
            _sentPos = cam.transform.position;
            _sentRot = cam.transform.rotation;
            int count = _count;
            cmd.RequestAsyncReadback(_result, count * 16, 0, r => Arrived(r, count));
        }

        private static void Arrived(AsyncGPUReadbackRequest request, int count)
        {
            _pending = false;
            if (request.hasError) return;
            if (_sentList != _listVersion || Time.frameCount - _sentFrame > MaxResultAge || count != _count) return;
            var data = request.GetData<Vector4>();
            var cam = Utils.GetMainCamera();
            bool fresh = cam != null && Vector3.Distance(cam.transform.position, _sentPos) <= StaleMeters
                && Quaternion.Angle(cam.transform.rotation, _sentRot) <= StaleDegrees;
            bool mayHide = OcclusionCulling.HidingNow && _partOn && fresh;
            int hiddenResults = 0;
            for (int i = 0; i < count && i < data.Length; i++)
            {
                var entry = Entries[i];
                if ((int)data[i].x == 0)
                {
                    hiddenResults++;
                    if (mayHide && ++entry.Streak >= HideAfter && !entry.Hidden) Hide(entry);
                    continue;
                }
                entry.Streak = 0;
                if (entry.Hidden) Show(entry);
            }
            if (_loggedStart) return;
            _loggedStart = true;
            Plugin.Log.LogInfo($"{Name}: first test done: {count} pieces with lights, {hiddenResults} with all their light behind the walls, first time this session");
        }

        private static void Hide(Entry entry)
        {
            foreach (var light in entry.Lights)
                if (light != null && light.enabled)
                {
                    light.enabled = false;
                    entry.OffLights.Add(light);
                }
            foreach (var flicker in entry.Flickers)
                if (flicker != null && flicker.enabled)
                {
                    flicker.enabled = false;
                    entry.OffFlickers.Add(flicker);
                }
            foreach (var system in entry.Systems)
                if (system != null && system.isPlaying)
                {
                    system.Pause(false);
                    entry.Paused.Add(system);
                }
            foreach (var flame in entry.Flames)
                if (flame != null && !flame.forceRenderingOff)
                {
                    flame.forceRenderingOff = true;
                    entry.OffFlames.Add(flame);
                }
            entry.Hidden = true;
            HiddenCount++;
            if (_loggedHide) return;
            _loggedHide = true;
            Plugin.Log.LogInfo($"{Name}: {entry.Piece.name} at {entry.Bounds.center} dark behind the walls ({entry.OffLights.Count} lights, {entry.Paused.Count} flames paused), first time this session");
        }

        // only what this changed is put back, and only where it is still as this left it. a light the game's light lod turned
        // down meanwhile (range 0) stays off
        private static void Show(Entry entry)
        {
            foreach (var light in entry.OffLights)
                if (light != null && !light.enabled && light.range > 0f) light.enabled = true;
            foreach (var flicker in entry.OffFlickers)
                if (flicker != null && !flicker.enabled) flicker.enabled = true;
            foreach (var system in entry.Paused)
                if (system != null && system.isPaused) system.Play(false);
            foreach (var flame in entry.OffFlames)
                if (flame != null && flame.forceRenderingOff) flame.forceRenderingOff = false;
            entry.OffLights.Clear();
            entry.OffFlickers.Clear();
            entry.Paused.Clear();
            entry.OffFlames.Clear();
            entry.Hidden = false;
            entry.Streak = 0;
            HiddenCount--;
        }

        internal static void ShowAll()
        {
            foreach (var entry in Cache.Values)
                if (entry.Hidden) Show(entry);
            foreach (var entry in Entries) entry.Streak = 0;
            HiddenCount = 0;
        }

        private sealed class SameObject : IEqualityComparer<Piece>
        {
            public bool Equals(Piece a, Piece b) => ReferenceEquals(a, b);
            public int GetHashCode(Piece p) => RuntimeHelpers.GetHashCode(p);
        }
    }
}
