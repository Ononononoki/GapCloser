using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace GapCloser.Fixes
{
    // newer, still being tested, with OcclusionCulling On: building pieces fully hidden from the sun (behind roofs and
    // walls, tested on the game's own sun shadow map) stop casting shadows. their shadow could only fall where a nearer
    // caster's shadow lies already, so nothing visible changes. pieces inside the range of a lamp or fire casting shadows
    // keep theirs. the test runs with occlusion's own piece list and bounds
    internal static class SunShadows
    {
        private const string Name = "SunShadowCulling";
        // a piece must lie this far (meters) behind the casters toward the sun, its box grows this much, plus texels for
        // the shadow filter
        private const float Margin = 0.5f;
        private const float Inflate = 0.25f;
        private const float BorderTexels = 2f;
        private const int MaxResultAge = 8;
        private const float CheckSeconds = 1f;
        // the map size before the gpu told it (medium quality, 2 cascades)
        private const int GuessW = 1024, GuessH = 512;

        private static ConfigEntry<bool> _enabled;
        private static SunShadowGpu _gpu;
        private static ComputeBuffer _result;
        private static bool _confirmed, _pending, _infoPending, _loggedAttach;
        private static float _nextCheck;
        private static int _sentFrame, _sentList;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _enabled = config.Bind(Sections.Fixes, Name, false,
                "With OcclusionCulling On: pieces hidden from the sun stop casting sun shadows. Off by default, it showed no gain in tests.");
        }

        // from OcclusionCulling's shader load: the sun shadow shaders of the same bundle
        internal static void Init(ComputeShader shader, Shader capture)
        {
            if (_enabled == null || !_enabled.Value) return;
            if (shader == null || capture == null || !capture.isSupported)
            {
                Plugin.Log.LogWarning($"{Name}: its shaders are missing in the bundle or not supported here, off");
                return;
            }
            _gpu = new SunShadowGpu(shader, capture);
            _gpu.Size(GuessW, GuessH);
            Plugin.Log.LogInfo($"{Name}: on, works while OcclusionCulling hides (margin {Margin} m toward the sun, after 3 hidden results in a row)");
        }

        // true when a test may run: the sun casts shadows, the command buffers sit on it, the map size is confirmed
        internal static bool Ready => _gpu != null && _gpu.Light != null && _confirmed;

        // from OcclusionCulling.Frame: once a second the sun, its shadows and the map size
        internal static void Frame()
        {
            if (_gpu == null || Time.time < _nextCheck) return;
            _nextCheck = Time.time + CheckSeconds;
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            bool shadows = sun != null && sun.isActiveAndEnabled && sun.shadows != LightShadows.None && QualitySettings.shadows != ShadowQuality.Disable;
            if (!shadows)
            {
                if (_gpu.Light != null)
                {
                    _gpu.Detach();
                    _confirmed = false;
                    OcclusionCulling.SunShowAll();
                }
                return;
            }
            if (_gpu.Light != sun)
            {
                _gpu.Attach(sun, SystemInfo.usesReversedZBuffer);
                _confirmed = false;
                OcclusionCulling.SunShowAll();
            }
            if (_infoPending) return;
            _infoPending = true;
            AsyncGPUReadback.Request(_gpu.Info, 16, 0, InfoArrived);
        }

        private static void InfoArrived(AsyncGPUReadbackRequest request)
        {
            _infoPending = false;
            if (request.hasError || _gpu == null || _gpu.Light == null) return;
            var info = request.GetData<uint>();
            int w = (int)info[0], h = (int)info[1];
            if (w <= 0 || h <= 0) return;
            if (w == _gpu.MapW && h == _gpu.MapH)
            {
                if (!_confirmed && !_loggedAttach)
                {
                    _loggedAttach = true;
                    Plugin.Log.LogInfo($"{Name}: on the sun {_gpu.Light.name}, shadow map {w} x {h}, {Cascades()} cascades, reversed z {SystemInfo.usesReversedZBuffer}, first time this session");
                }
                _confirmed = true;
                return;
            }
            // a new size: a matching pyramid and new command buffers, tests wait for the next confirmation
            var sun = _gpu.Light;
            _gpu.Size(w, h);
            _gpu.Attach(sun, SystemInfo.usesReversedZBuffer);
            _confirmed = false;
            OcclusionCulling.SunShowAll();
        }

        private static int Cascades() => Mathf.Clamp(QualitySettings.shadowCascades, 1, 4);

        // from OcclusionCulling's dispatch: the test of the same piece bounds, read back in the same command buffer
        internal static void Record(CommandBuffer cmd, ComputeBuffer bounds, int count, int listVersion)
        {
            if (!Ready || _pending || count == 0) return;
            if (_result == null || _result.count < count)
            {
                _result?.Release();
                _result = new ComputeBuffer(Math.Max(1024, count * 3 / 2), 16);
            }
            _gpu.RecordTest(cmd, bounds, count, Cascades(), Inflate, Margin, BorderTexels, _result);
            _pending = true;
            _sentFrame = Time.frameCount;
            _sentList = listVersion;
            cmd.RequestAsyncReadback(_result, count * 16, 0, r => Arrived(r, count));
        }

        private static void Arrived(AsyncGPUReadbackRequest request, int count)
        {
            _pending = false;
            if (request.hasError || !Ready || Time.frameCount - _sentFrame > MaxResultAge) return;
            OcclusionCulling.SunResults(request.GetData<Vector4>(), count, _sentList);
        }
    }
}
