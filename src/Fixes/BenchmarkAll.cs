using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // one key for every benchmark: stand still and press it. the parts of GapCloser that work every frame (hiding from the
    // camera, sun shadows, hidden lights, merged areas) are compared with all of them off (the baseline, no depth test
    // at all): first everything together, then each part alone. 40 s each (2 s and then 5 s phases), a part that can not
    // run here is left out, every
    // part is put back as it was at the end. every result goes to the log and to the benchmark file. pressing it again
    // stops the run after the benchmark that is running
    internal static class BenchmarkAll
    {
        private const string Name = "Benchmarks";
        private const float GapSeconds = 1f;

        private sealed class Part
        {
            public string Label;
            public Func<bool> Available, Get;
            public Action<bool> Set;
        }

        private static readonly Part[] Parts =
        {
            new Part { Label = "camera hiding", Available = () => OcclusionCulling.CameraPartAvailable, Get = () => OcclusionCulling.CameraPart, Set = OcclusionCulling.SetCameraPart },
            new Part { Label = "sun shadows", Available = () => OcclusionCulling.SunPartAvailable, Get = () => OcclusionCulling.SunPart, Set = OcclusionCulling.SetSunPart },
            new Part { Label = "hidden lights", Available = () => HiddenLights.PartAvailable, Get = () => HiddenLights.PartOn, Set = HiddenLights.SetPart },
            new Part { Label = "merged areas", Available = () => PieceBatching.PartAvailable, Get = () => PieceBatching.PartOn, Set = PieceBatching.SetPart },
        };

        private sealed class Step
        {
            public string Label;
            public Func<bool> Start;
        }

        private static ConfigEntry<KeyboardShortcut> _key;
        private static readonly List<Step> Steps = new List<Step>();
        private static readonly List<Part> Here = new List<Part>();
        private static readonly bool[] Saved = new bool[Parts.Length];
        private static readonly List<string> Ran = new List<string>(), LeftOut = new List<string>();
        private static bool _running, _stopAfter, _savedHiding;
        private static int _step;
        private static float _nextAt;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _key = config.Bind(Sections.Debug, "BenchmarkKey", new KeyboardShortcut(KeyCode.End, KeyCode.LeftShift),
                "Stand still and press it: GapCloser's parts are measured against all of them off, a few minutes, the results in the log and in BepInEx/GapCloser_benchmarks.txt. Press again to stop.");
            Benchmark.Start();
            Plugin.Log.LogInfo($"{Name}: {_key.Value} runs every benchmark one after another");
        }

        // once a frame from the plugin, after every fix that runs a benchmark
        internal static void Frame()
        {
            if (_key == null) return;
            bool pressed = _key.Value.IsDown() && !OcclusionCulling.Typing();
            if (!_running)
            {
                if (pressed && !Benchmark.Running) Begin();
                return;
            }
            if (pressed && !_stopAfter)
            {
                _stopAfter = true;
                if (MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, "GapCloser benchmarks: stopping after this one");
            }
            if (Benchmark.Running)
            {
                _nextAt = Time.realtimeSinceStartup + GapSeconds;
                return;
            }
            if (Time.realtimeSinceStartup < _nextAt) return;
            if (_stopAfter) End("stopped");
            else Next();
        }

        private static void Begin()
        {
            Here.Clear();
            for (int i = 0; i < Parts.Length; i++)
            {
                Saved[i] = Parts[i].Get();
                if (Parts[i].Available()) Here.Add(Parts[i]);
            }
            _savedHiding = OcclusionCulling.Hiding;
            Steps.Clear();
            if (Here.Count > 0)
            {
                Steps.Add(new Step { Label = "everything", Start = () => PartsRun("everything", Here) });
                if (Here.Count > 1)
                    foreach (var part in Here)
                    {
                        var alone = new List<Part> { part };
                        Steps.Add(new Step { Label = part.Label + " alone", Start = () => PartsRun(part.Label + " alone", alone) });
                    }
            }
            Ran.Clear();
            LeftOut.Clear();
            _running = true;
            _stopAfter = false;
            _step = -1;
            Plugin.Log.LogInfo($"{Name}: run started at {DateTime.Now:HH:mm:ss}, {Steps.Count} steps, parts here: {(Here.Count > 0 ? string.Join(", ", Here.ConvertAll(p => p.Label)) : "none")}");
            Next();
        }

        // the chosen parts on in even phases, every part of here off in odd ones (the baseline), the hiding switched on
        private static bool PartsRun(string label, List<Part> chosen)
        {
            OcclusionCulling.SetHidingForRun(true);
            return Benchmark.Begin("Benchmark " + label, "on", "baseline", on =>
            {
                foreach (var part in Here) part.Set(on && chosen.Contains(part));
            }, false);
        }

        private static void Next()
        {
            while (++_step < Steps.Count)
            {
                var step = Steps[_step];
                Benchmark.StepText = $"{_step + 1} of {Steps.Count}: {step.Label}";
                if (step.Start())
                {
                    Ran.Add(step.Label);
                    return;
                }
                LeftOut.Add(step.Label);
            }
            End(null);
        }

        // every part and the hiding as they were before the run
        private static void Restore()
        {
            for (int i = 0; i < Parts.Length; i++)
                if (Parts[i].Get() != Saved[i]) Parts[i].Set(Saved[i]);
            OcclusionCulling.SetHidingForRun(_savedHiding);
        }

        private static void End(string reason)
        {
            Restore();
            _running = false;
            Benchmark.StepText = null;
            Benchmark.Record($"{Name}: run {reason ?? "done"}, ran {(Ran.Count > 0 ? string.Join(", ", Ran) : "nothing")}{(LeftOut.Count > 0 ? $", left out here: {string.Join(", ", LeftOut)}" : "")}");
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, reason == null ? $"GapCloser benchmarks: all {Ran.Count} done, results in GapCloser_benchmarks.txt" : $"GapCloser benchmarks stopped after {Ran.Count}");
        }
    }
}
