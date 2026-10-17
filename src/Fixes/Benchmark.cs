using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;

namespace GapCloser.Fixes
{
    // a shared a/b benchmark for the fixes that switch something while playing (the occlusion hiding, the camera sort):
    // 10 phases of 2 s, the switch on in even phases and off in odd ones, the first 0.6 s of each phase left out. frame
    // times come from a stopwatch (unity's delta time is paced in steps of the screen refresh), the main camera's render
    // time on the cpu from its cull to the end of its render, and unity's draw call, set pass and triangle counters
    internal static class Benchmark
    {
        private const float PhaseSeconds = 2f;
        private const float SettleSeconds = 0.6f;
        private const int Phases = 10;

        private static bool _started;
        private static readonly Stopwatch FrameClock = Stopwatch.StartNew(), RenderClock = new Stopwatch();
        private static double _lastFrameAt = -1;
        private static ProfilerRecorder _drawCalls, _setPass, _triangles;

        // the last frame's numbers
        public static float FrameMs { get; private set; }
        public static double RenderMs { get; private set; }
        public static long DrawCalls => Counter(_drawCalls);
        public static long SetPass => Counter(_setPass);
        public static long Triangles => Counter(_triangles);

        // the running benchmark
        private static string _name, _onLabel, _offLabel, _extraLabel;
        private static Action<bool> _set;
        private static Func<double> _extra;
        private static bool _wasOn;
        private static float _start = -1f, _moved, _turned;
        private static int _phase = -1;
        private static Vector3 _pos;
        private static Quaternion _rot;
        // per condition (0 off, 1 on): frame and render times, and sums of draw calls, set pass calls, triangles, the extra
        private static readonly List<float>[] Ms = { new List<float>(), new List<float>() };
        private static readonly List<float>[] Render = { new List<float>(), new List<float>() };
        private static readonly double[] Draw = new double[2], Pass = new double[2], Tris = new double[2], Extra = new double[2];

        public static bool Running => _start >= 0f;

        // the clocks and counters start with the first fix that needs them
        public static void Start()
        {
            if (_started) return;
            _started = true;
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
        }

        private static long Counter(ProfilerRecorder recorder) => recorder.Valid ? recorder.LastValue : -1;

        private static void OnPreCull(Camera cam)
        {
            if (cam == Utils.GetMainCamera()) RenderClock.Restart();
        }

        private static void OnPostRender(Camera cam)
        {
            if (!RenderClock.IsRunning || cam != Utils.GetMainCamera()) return;
            RenderMs = RenderClock.Elapsed.TotalMilliseconds;
            RenderClock.Stop();
        }

        // once a frame, before the fixes that read it
        internal static void Frame()
        {
            if (!_started) return;
            double now = FrameClock.Elapsed.TotalMilliseconds;
            FrameMs = _lastFrameAt < 0 ? 0f : (float)(now - _lastFrameAt);
            _lastFrameAt = now;
            if (Running) Step();
        }

        // set switches the thing on or off, wasOn is put back at the end; extra is averaged over the on phases
        public static bool Begin(string name, string onLabel, string offLabel, Action<bool> set, bool wasOn, Func<double> extra = null, string extraLabel = null)
        {
            if (!_started || Running) return false;
            var cam = Utils.GetMainCamera();
            if (cam == null) return false;
            _name = name;
            _onLabel = onLabel;
            _offLabel = offLabel;
            _set = set;
            _wasOn = wasOn;
            _extra = extra;
            _extraLabel = extraLabel;
            _start = Time.realtimeSinceStartup;
            _phase = -1;
            _pos = cam.transform.position;
            _rot = cam.transform.rotation;
            _moved = _turned = 0f;
            for (int k = 0; k < 2; k++)
            {
                Ms[k].Clear();
                Render[k].Clear();
                Draw[k] = Pass[k] = Tris[k] = Extra[k] = 0;
            }
            Plugin.Log.LogInfo($"{_name}: benchmark started at {DateTime.Now:HH:mm:ss}, ({_pos.x:0}, {_pos.z:0})");
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"GapCloser {_name} benchmark: stand still {Phases * PhaseSeconds:0} s");
            return true;
        }

        private static void Step()
        {
            float t = Time.realtimeSinceStartup - _start;
            int phase = (int)(t / PhaseSeconds);
            if (phase >= Phases)
            {
                Finish();
                return;
            }
            bool on = phase % 2 == 0;
            if (phase != _phase)
            {
                _phase = phase;
                _set(on);
            }
            var cam = Utils.GetMainCamera();
            if (cam != null)
            {
                _moved = Mathf.Max(_moved, Vector3.Distance(cam.transform.position, _pos));
                _turned = Mathf.Max(_turned, Quaternion.Angle(cam.transform.rotation, _rot));
            }
            if (t - phase * PhaseSeconds < SettleSeconds) return;
            int k = on ? 1 : 0;
            Ms[k].Add(FrameMs);
            Render[k].Add((float)RenderMs);
            Draw[k] += DrawCalls;
            Pass[k] += SetPass;
            Tris[k] += Triangles;
            if (_extra != null) Extra[k] += _extra();
        }

        private static void Finish()
        {
            _start = -1f;
            _set(_wasOn);
            int nOff = Ms[0].Count, nOn = Ms[1].Count;
            if (nOff == 0 || nOn == 0)
            {
                Plugin.Log.LogInfo($"{_name}: benchmark ended without frames to compare");
                return;
            }
            float Median(List<float> list)
            {
                list.Sort();
                return list[list.Count / 2];
            }
            float P95(List<float> list) => list[Math.Min(list.Count - 1, (int)(list.Count * 0.95f))];
            double MeanOf(List<float> list)
            {
                double sum = 0;
                foreach (float v in list) sum += v;
                return sum / list.Count;
            }
            double offMean = MeanOf(Ms[0]), onMean = MeanOf(Ms[1]);
            float offRender = Median(Render[0]), onRender = Median(Render[1]);
            float offMedian = Median(Ms[0]), onMedian = Median(Ms[1]);
            string extra = _extra != null ? $", {Extra[1] / nOn:0} {_extraLabel}" : "";
            Plugin.Log.LogInfo($"{_name}: BENCHMARK at ({_pos.x:0}, {_pos.z:0}), moved {_moved:0.0} m, turned {_turned:0} deg: " +
                $"{_onLabel} {onMedian:0.00} ms median ({onMean:0.00} mean, {P95(Ms[1]):0.0} p95, {nOn} frames{extra}), " +
                $"{_offLabel} {offMedian:0.00} ms median ({offMean:0.00} mean, {P95(Ms[0]):0.0} p95, {nOff} frames); " +
                $"{_onLabel} minus {_offLabel}: {onMedian - offMedian:+0.00;-0.00} ms median, {onMean - offMean:+0.00;-0.00} ms mean, " +
                $"draw calls {Draw[1] / nOn - Draw[0] / nOff:+0;-0} (of {Draw[0] / nOff:0}), set pass {Pass[1] / nOn - Pass[0] / nOff:+0;-0} (of {Pass[0] / nOff:0}), " +
                $"triangles {(Tris[1] / nOn - Tris[0] / nOff) / 1000:+0;-0}k; " +
                $"main camera render {onRender:0.00} ms median {_onLabel}, {offRender:0.00} {_offLabel} ({onRender - offRender:+0.00;-0.00})");
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"{_name} benchmark done: {_onLabel} {onMedian:0.0} ms, {_offLabel} {offMedian:0.0} ms");
        }
    }
}
