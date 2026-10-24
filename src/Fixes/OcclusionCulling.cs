using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace GapCloser.Fixes
{
    // experimental: tests every still building piece against the camera's own depth image on the gpu (a pyramid of the
    // farthest depth per block, so a test can only err toward "visible"). On takes a piece out of the camera's view after
    // 3 hidden results in a row (its shadow stays) and puts it back at its first visible result. LookOnly only counts
    internal static class OcclusionCulling
    {
        internal enum Mode { Off, LookOnly, On }

        private const string Name = "OcclusionCulling";
        private const string BundleResource = "GapCloser.gapcloser_occlusion";
        // occluders nearer than this (meters) do not count: the player's own body, held items, first person arms
        private const float IgnoreNear = 3f;
        // a piece must lie this far behind the occluders (meters, plus a share of its distance) to count as hidden
        private const float Margin = 0.5f;
        private const float MarginScale = 0.02f;
        // boxes grow by the camera's speed times this (seconds), at least MinInflate meters, against the readback delay
        private const float InflateSeconds = 0.15f;
        private const float MinInflate = 0.25f;
        // different prefab names of left out pieces collected before the one log line about them
        private const int NotBuildingSample = 6;
        // hidden results in a row before a piece is hidden
        private const int HideAfter = 3;
        // results older than this many frames are dropped, hidden results are ignored when the camera moved or turned
        // this much since the test
        private const int MaxResultAge = 8;
        private const float StaleMeters = 0.75f;
        private const float StaleDegrees = 8f;
        // a camera jump (teleport, respawn, loading) or a fast turn between two frames puts everything back for a moment
        internal const float JumpMeters = 5f;
        internal const float JumpDegrees = 30f;
        private const float PauseSeconds = 0.5f;
        private const float ListSeconds = 1f;
        // new pieces are read for at most this long a frame (at least a few), the rest in the next frames
        private const float ReadBudgetMs = 1f;
        private const int MinNewPieces = 20;
        private const int TestEveryFrames = 2;
        private const float StatSeconds = 30f;
        private const int MaxCached = 30000;
        // the plain log line period
        private const float LineSeconds = 5f;
        // the pyramid level written with a snapshot is the first one at most this wide
        private const int SnapshotLevelWidth = 200;
        // a piece this near (meters past the range) to a lamp or fire casting shadows keeps its shadow; a light counts as
        // moved or grown after this much
        private const float LitMargin = 0.5f;

        private static ConfigEntry<Mode> _mode;
        private static ConfigEntry<KeyboardShortcut> _key, _benchKey;
        private static ConfigEntry<bool> _logLines;
        private static float _nextLine;
        private static int _lineFrames, _lastInView, _lastHiddenResults;
        private static double _lineMs, _lineMaxMs, _lineDraw, _lineSetPass, _lineRender;
        private static bool _hiding;
        private static ComputeShader _shader;
        private static OcclusionGpu _gpu;
        private static CommandBuffer _cmd;
        private static ComputeBuffer _bounds, _result, _rect;
        private static AccessTools.FieldRef<List<Piece>> _allPieces;

        // a still piece: its bounds read once, every mesh renderer under it, and what was changed on them while hidden
        private sealed class Entry
        {
            public Piece Piece;
            public Bounds Bounds;
            public Renderer[] Renderers;
            public ShadowCastingMode[] Modes;
            // 0 untouched, 1 set to shadows only (Modes holds the old mode), 2 rendering turned off (it cast no shadow)
            public byte[] Changed;
            public bool Hidden;
            public int Streak;
            public int SeenList;
            // the sun shadow test: hidden from the sun after 3 results in a row; SunChanged 0 untouched, 3 shadow casting
            // set off (SunModes holds the old mode), 4 rendering off (a renderer that only casts shadows)
            public bool SunHidden;
            public int SunStreak;
            public ShadowCastingMode[] SunModes;
            public byte[] SunChanged;
            // drawn in a merged mesh of PieceBatching: both tests leave it alone
            public bool Batched;
            // inside the range of a lamp or fire casting shadows now (never hidden from the sun then), for one light version
            public bool Lit;
            public int LitVersion = -1;
            // a merged mesh that came apart: both tests skip it until the next list
            public bool Gone;
        }

        private static readonly Dictionary<Piece, Entry> Cache = new Dictionary<Piece, Entry>(new SameObject());
        // PieceBatching's merged meshes, tested like pieces (no piece, the mesh's own bounds)
        private static readonly Dictionary<Renderer, Entry> BatchCache = new Dictionary<Renderer, Entry>(new SameRenderer());
        private static readonly List<Renderer> DeadBatches = new List<Renderer>();
        // the point and spot lights (read with the list), the ones casting shadows at the last change (center, range) and
        // this frame's
        private static readonly List<Light> LocalLights = new List<Light>();
        private static readonly List<Vector4> ShadowLights = new List<Vector4>();
        private static readonly List<Vector4> LightsNow = new List<Vector4>();
        private static int _lightVersion;
        private static readonly HashSet<Piece> Skipped = new HashSet<Piece>(new SameObject());
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<Vector4> BoundsData = new List<Vector4>();
        private static readonly List<Piece> Stale = new List<Piece>();
        private static int _count, _listVersion, _frame;
        private static float _nextList;
        private static bool _backlog;
        private static readonly Stopwatch ReadWatch = new Stopwatch();

        // the pieces hidden right now (the probe reads it for its per frame csv)
        public static int HiddenCount;

        // true when the depth test runs with On (the shadow benchmark needs it)
        internal static bool HidingAvailable => _shader != null && _mode.Value == Mode.On;

        // true while pieces may be hidden now: the test runs, hiding is switched on, no pause after a camera jump
        internal static bool HidingNow => _shader != null && _hiding && Time.time >= _pauseUntil;

        // for the shadow benchmark: every renderer of the tracked pieces, and the ones this fix set to shadows only now
        internal static void TrackedRenderers(List<Renderer> all, List<Renderer> hidden)
        {
            all.Clear();
            hidden.Clear();
            foreach (var entry in Entries)
                for (int k = 0; k < entry.Renderers.Length; k++)
                {
                    all.Add(entry.Renderers[k]);
                    if (entry.Hidden && entry.Changed[k] == 1) hidden.Add(entry.Renderers[k]);
                }
        }

        // the camera between frames, for jumps and for the box growth
        private static Vector3 _lastPos;
        private static Quaternion _lastRot;
        private static float _speed, _pauseUntil;
        private static bool _haveLast;

        // the test in flight
        private static bool _pending;
        private static int _sentList, _sentFrame;
        private static Vector3 _sentPos;
        private static Quaternion _sentRot;

        // stats
        private static readonly Stopwatch Watch = new Stopwatch();
        private static double _dispatchMs, _applyMs;
        private static int _tests, _testedSum, _inViewSum, _hiddenResultSum, _hides, _shows, _jumps, _dropped, _staleTests;
        private static float _nextStat;
        private static bool _loggedSource, _loggedFirst, _loggedHide;
        private static string _lastSummary = "no test finished yet";

        // a snapshot asked for by the probe at a marker: written when its readbacks arrive
        private static string _snapshotFolder;

        private sealed class Snap
        {
            public string Folder;
            public List<Piece> Pieces;
            public List<Entry> Items;
            public Matrix4x4 WorldToClip, WorldToCamera;
            public string Camera;
            public Vector4[] Result, Rect;
            public float[] Level;
            public int LevelIndex, LevelW, LevelH, Waiting;
        }

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _mode = config.Bind(Sections.Fixes, Name, Mode.On,
                "Building pieces the walls fully hide are not drawn, their shadows stay. On, LookOnly (logs only) or Off. Checked on DirectX 11.");
            _key = config.Bind(Sections.Debug, Name + "Key", new KeyboardShortcut(KeyCode.Home),
                "Switches occlusion culling while playing, to compare.");
            _benchKey = config.Bind(Sections.Debug, Name + "BenchmarkKey", new KeyboardShortcut(KeyCode.Home, KeyCode.LeftShift),
                "Stand still and press it: 20 s of occlusion culling on and off, the result in the log.");
            _logLines = config.Bind(Sections.Debug, Name + "LogLines", false,
                "A log line every 5 s while occlusion culling runs.");
            if (_mode.Value == Mode.Off || !Plugin.GameMatches(Name)) return;
            // the depth image's layout was checked on Direct3D 11 only, another graphics API may store it flipped
            var api = SystemInfo.graphicsDeviceType;
            if (api != GraphicsDeviceType.Direct3D11)
            {
                if (!Plugin.ForceOnUntested)
                {
                    Plugin.Log.LogInfo($"{Name}: checked on DirectX 11 only, this game runs on {api}, off (ForceOnUntestedVersions turns it on)");
                    return;
                }
                Plugin.Log.LogWarning($"{Name}: checked on DirectX 11 only, this game runs on {api}, forced on by ForceOnUntestedVersions");
            }
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            {
                Plugin.Log.LogWarning($"{Name}: this graphics device has no compute shaders or no async readback, off");
                return;
            }
            var field = AccessTools.Field(typeof(Piece), "s_allPieces");
            if (field == null || !field.IsStatic || field.FieldType != typeof(List<Piece>))
            {
                Plugin.Log.LogWarning($"{Name}: the game's piece list (Piece.s_allPieces) not found, off");
                return;
            }
            _allPieces = AccessTools.StaticFieldRefAccess<List<Piece>>(field);
            if (!LoadShader()) return;
            _hiding = _mode.Value == Mode.On;
            Benchmark.Start();
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            Plugin.Log.LogInfo($"{Name}: {_mode.Value} on{(_mode.Value == Mode.On ? $", {_key.Value} switches the hiding" : ", hides nothing")} (ignores occluders nearer than {IgnoreNear} m, margin {Margin} m plus {MarginScale:P0} of the distance, hides after {HideAfter} hidden results in a row)");
        }

        private static bool LoadShader()
        {
            try
            {
                byte[] bytes;
                using (var stream = typeof(OcclusionCulling).Assembly.GetManifestResourceStream(BundleResource))
                {
                    if (stream == null) throw new InvalidOperationException("embedded bundle missing");
                    bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                }
                var bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null) throw new InvalidOperationException("bundle did not load");
                _shader = bundle.LoadAsset<ComputeShader>("GapCloserHiZ");
                var sunShader = bundle.LoadAsset<ComputeShader>("GapCloserShadow");
                var sunCapture = bundle.LoadAsset<Shader>("GapCloserShadowMatrices");
                bundle.Unload(false);
                SunShadows.Init(sunShader, sunCapture);
                if (_shader == null) throw new InvalidOperationException("no GapCloserHiZ in the bundle");
                _gpu = new OcclusionGpu(_shader);
                _cmd = new CommandBuffer { name = "GapCloser occlusion" };
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: the depth test shader could not load ({e.GetType().Name}: {e.Message}), off");
                return false;
            }
        }

        // the probe calls this at a marker (by reflection): the next test's results, the camera, a small pyramid level and a
        // screenshot go to the folder
        public static string Snapshot(string folder)
        {
            if (_shader == null) return "occlusion depth test: off";
            Directory.CreateDirectory(folder);
            _snapshotFolder = folder;
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, "screenshot.png"));
            return $"occlusion depth test ({(_hiding ? "hiding" : "not hiding")}, {HiddenCount} hidden now), last {StatSeconds:0} s: {_lastSummary}";
        }

        // every frame: the hotkey, the camera jump rule; once a second the candidate list from the game's piece list
        internal static void Frame()
        {
            if (_shader == null) return;
            SunShadows.Frame();
            if ((!HidingNow || !_sunPart) && SunHiddenCount > 0) SunShowAll();
            if (_mode.Value == Mode.On && !Typing())
            {
                if (_benchKey.Value.IsDown()) StartBenchmark();
                else if (_key.Value.IsDown() && !Benchmark.Running) Switch();
            }
            var cam = Utils.GetMainCamera();
            if (cam != null)
            {
                var pos = cam.transform.position;
                var rot = cam.transform.rotation;
                if (_haveLast)
                {
                    float moved = Vector3.Distance(pos, _lastPos), turned = Quaternion.Angle(rot, _lastRot);
                    if (moved > JumpMeters || turned > JumpDegrees)
                    {
                        if (HiddenCount > 0) ShowAll();
                        _pauseUntil = Time.time + PauseSeconds;
                        _jumps++;
                    }
                    float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
                    _speed = Mathf.Lerp(_speed, moved / dt, 0.2f);
                }
                _lastPos = pos;
                _lastRot = rot;
                _haveLast = true;
            }
            Line();
            if (Time.time < _nextList)
            {
                if (_backlog) ReadNew(_allPieces());
                return;
            }
            _nextList = Time.time + ListSeconds;
            RebuildList();
        }

        // the parts the run of all benchmarks switches on their own: hiding from the camera, shadows off for the sun
        private static bool _cameraPart = true, _sunPart = true;

        internal static bool CameraPartAvailable => _shader != null && _mode.Value == Mode.On;
        internal static bool SunPartAvailable => CameraPartAvailable && SunShadows.Ready;
        internal static bool CameraPart => _cameraPart;
        internal static bool SunPart => _sunPart;
        internal static bool Hiding => _hiding;

        internal static void SetCameraPart(bool on)
        {
            _cameraPart = on;
            if (!on && HiddenCount > 0) ShowAll();
            foreach (var entry in Entries) entry.Streak = 0;
        }

        internal static void SetSunPart(bool on)
        {
            _sunPart = on;
            if (!on && SunHiddenCount > 0) SunShowAll();
            foreach (var entry in Entries) entry.SunStreak = 0;
        }

        // the home key's switch, for the run of all benchmarks (on while it runs, put back after)
        internal static void SetHidingForRun(bool on)
        {
            if (_shader != null && _hiding != on) SetHiding(on);
        }

        // the hiding on and off every 2 s for 20 s (its own key and the run of all benchmarks); false when it can not run
        internal static bool StartBenchmark()
        {
            if (_shader == null || _mode.Value != Mode.On) return false;
            return Benchmark.Begin(Name, "hiding on", "off", SetHiding, _hiding, () => HiddenCount, "hidden");
        }

        internal static bool Typing() =>
            global::Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus()) || Menu.IsVisible();

        private static void Switch()
        {
            _hiding = !_hiding;
            if (!_hiding && HiddenCount > 0) ShowAll();
            foreach (var entry in Entries) entry.Streak = 0;
            Plugin.Log.LogInfo($"{Name}: hiding switched {(_hiding ? "on" : "off")} at {DateTime.Now:HH:mm:ss}");
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, $"GapCloser occlusion culling {(_hiding ? "ON" : "OFF")}");
        }

        // the benchmark's switch: hiding on or off, every piece put back when off
        private static void SetHiding(bool on)
        {
            _hiding = on;
            if (!on && HiddenCount > 0) ShowAll();
            foreach (var entry in Entries) entry.Streak = 0;
        }

        // a plain line every few seconds while it runs: where, how many hidden, frame time and draw calls
        private static void Line()
        {
            if (!_logLines.Value) return;
            float dt = Benchmark.FrameMs;
            _lineRender += Benchmark.RenderMs;
            _lineFrames++;
            _lineMs += dt;
            if (dt > _lineMaxMs) _lineMaxMs = dt;
            _lineDraw += Benchmark.DrawCalls;
            _lineSetPass += Benchmark.SetPass;
            if (Time.realtimeSinceStartup < _nextLine) return;
            _nextLine = Time.realtimeSinceStartup + LineSeconds;
            var player = Player.m_localPlayer;
            if (player != null && _lineFrames > 0)
            {
                var p = player.transform.position;
                Plugin.Log.LogInfo($"{Name} {LineSeconds:0} s: at ({p.x:0}, {p.z:0}) {(Benchmark.Running ? "benchmark" : _hiding ? "hiding" : "not hiding")}, {HiddenCount} hidden, " +
                                   $"{_lastInView} in view, {_lastHiddenResults} behind walls; frame {_lineMs / _lineFrames:0.0} ms (max {_lineMaxMs:0}), render {_lineRender / _lineFrames:0.0} ms, draw calls {_lineDraw / _lineFrames:0}, set pass {_lineSetPass / _lineFrames:0}");
            }
            _lineFrames = 0;
            _lineMs = _lineMaxMs = _lineDraw = _lineSetPass = _lineRender = 0;
        }

        // reads the pieces not read yet, within the frame's budget; while some wait it goes on every frame
        private static void ReadNew(List<Piece> all)
        {
            if (all == null) return;
            ReadWatch.Restart();
            _backlog = false;
            int fresh = 0;
            foreach (var piece in all)
            {
                if (piece == null || Cache.ContainsKey(piece) || Skipped.Contains(piece)) continue;
                if (fresh >= MinNewPieces && ReadWatch.Elapsed.TotalMilliseconds >= ReadBudgetMs)
                {
                    _backlog = true;
                    return;
                }
                fresh++;
                var entry = Read(piece);
                if (entry == null)
                {
                    Skipped.Add(piece);
                    continue;
                }
                if (Cache.Count >= MaxCached && HiddenCount == 0 && SunHiddenCount == 0)
                {
                    Cache.Clear();
                    Skipped.Clear();
                }
                Cache[piece] = entry;
            }
        }

        private static void RebuildList()
        {
            var all = _allPieces();
            if (all == null) return;
            ReadNew(all);
            LocalLights.Clear();
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light.type == LightType.Point || light.type == LightType.Spot) LocalLights.Add(light);
            _listVersion++;
            Entries.Clear();
            BoundsData.Clear();
            foreach (var piece in all)
            {
                if (piece == null || !Cache.TryGetValue(piece, out var entry)) continue;
                entry.SeenList = _listVersion;
                Entries.Add(entry);
                BoundsData.Add(entry.Bounds.center);
                BoundsData.Add(entry.Bounds.extents);
            }
            // the merged meshes after the pieces; one destroyed without word (a new world) is put back and forgotten
            DeadBatches.Clear();
            foreach (var pair in BatchCache)
            {
                if (pair.Key == null)
                {
                    DeadBatches.Add(pair.Key);
                    continue;
                }
                pair.Value.SeenList = _listVersion;
                Entries.Add(pair.Value);
                BoundsData.Add(pair.Value.Bounds.center);
                BoundsData.Add(pair.Value.Bounds.extents);
            }
            foreach (var renderer in DeadBatches) RemoveBatch(renderer);
            // pieces gone from the game's list: put back if still there (a hidden piece must never stay hidden), forget them
            Stale.Clear();
            foreach (var pair in Cache)
                if (pair.Value.SeenList != _listVersion) Stale.Add(pair.Key);
            foreach (var piece in Stale)
            {
                var entry = Cache[piece];
                if (entry.SunHidden) SunShow(entry);
                if (entry.Hidden) Show(entry);
                Cache.Remove(piece);
            }
            _count = Entries.Count;
            if (_count == 0) return;
            if (_bounds == null || _bounds.count < BoundsData.Count)
            {
                Release(ref _bounds);
                Release(ref _result);
                Release(ref _rect);
                int capacity = Math.Max(1024, _count * 3 / 2);
                _bounds = new ComputeBuffer(capacity * 2, 16);
                _result = new ComputeBuffer(capacity, 16);
                _rect = new ComputeBuffer(capacity, 16);
            }
            _bounds.SetData(BoundsData);
        }

        // a still piece once: left out are pieces without a valid network object (the placement ghost), under a rigidbody
        // (ships, carts), with moving parts (doors, anything animated), without a mesh, or without wear and tear (plants,
        // saplings, pickables and debris that PlantEverything turns into pieces: they grow, get picked and regrow)
        private static Entry Read(Piece piece)
        {
            var view = piece.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return null;
            if (piece.GetComponent<WearNTear>() == null)
            {
                NotBuilding(piece);
                return null;
            }
            if (piece.GetComponentInParent<Rigidbody>() != null || piece.GetComponent<Door>() != null || piece.GetComponentInChildren<Animator>(true) != null) return null;
            var renderers = new List<Renderer>();
            Bounds bounds = default;
            bool any = false;
            foreach (var renderer in piece.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                renderers.Add(renderer);
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            if (!any) return null;
            return new Entry
            {
                Piece = piece,
                Bounds = bounds,
                Renderers = renderers.ToArray(),
                Modes = new ShadowCastingMode[renderers.Count],
                Changed = new byte[renderers.Count],
                SunModes = new ShadowCastingMode[renderers.Count],
                SunChanged = new byte[renderers.Count],
            };
        }

        // counts the pieces left out for having no wear and tear, and names the first few once a session
        private static int _notBuilding;
        private static readonly List<string> NotBuildingNames = new List<string>();
        private static bool _loggedNotBuilding;

        private static void NotBuilding(Piece piece)
        {
            _notBuilding++;
            if (_loggedNotBuilding) return;
            string name = Utils.GetPrefabName(piece.gameObject);
            if (!NotBuildingNames.Contains(name)) NotBuildingNames.Add(name);
            if (NotBuildingNames.Count < NotBuildingSample) return;
            _loggedNotBuilding = true;
            Plugin.Log.LogInfo($"{Name}: pieces without wear and tear are left out (plants, pickables, debris), {_notBuilding} so far, for example {string.Join(", ", NotBuildingNames)}, first time this session");
        }

        private static void Hide(Entry entry)
        {
            for (int k = 0; k < entry.Renderers.Length; k++)
            {
                var renderer = entry.Renderers[k];
                if (renderer == null || renderer.forceRenderingOff) continue;
                var mode = renderer.shadowCastingMode;
                if (mode == ShadowCastingMode.ShadowsOnly) continue;
                if (mode == ShadowCastingMode.Off)
                {
                    renderer.forceRenderingOff = true;
                    entry.Changed[k] = 2;
                }
                else
                {
                    entry.Modes[k] = mode;
                    renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    entry.Changed[k] = 1;
                }
            }
            entry.Hidden = true;
            HiddenCount++;
            _hides++;
            if (!_loggedHide)
            {
                _loggedHide = true;
                Plugin.Log.LogInfo($"{Name}: hid {Label(entry)} at {entry.Bounds.center} behind the walls, first time this session");
            }
        }

        // only what this fix changed is put back, and only where it is still as this fix left it
        private static void Show(Entry entry)
        {
            for (int k = 0; k < entry.Renderers.Length; k++)
            {
                var renderer = entry.Renderers[k];
                byte changed = entry.Changed[k];
                entry.Changed[k] = 0;
                if (renderer == null || changed == 0) continue;
                if (changed == 2 && renderer.forceRenderingOff) renderer.forceRenderingOff = false;
                else if (changed == 1 && renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) renderer.shadowCastingMode = entry.Modes[k];
            }
            entry.Hidden = false;
            entry.Streak = 0;
            HiddenCount--;
            _shows++;
        }

        private static void ShowAll()
        {
            foreach (var entry in Cache.Values)
                if (entry.Hidden) Show(entry);
            foreach (var entry in BatchCache.Values)
                if (entry.Hidden) Show(entry);
            foreach (var entry in Entries) entry.Streak = 0;
            if (HiddenCount != 0)
            {
                Plugin.Log.LogWarning($"{Name}: {HiddenCount} pieces counted hidden after putting everything back, count reset");
                HiddenCount = 0;
            }
        }

        // the pieces whose shadows are off because the sun can not see them (the probe reads it for its per frame csv)
        public static int SunHiddenCount;
        private static int _sunHides, _sunShows, _sunTests;
        private static bool _loggedSunFirst, _loggedSunHide;

        // SunShadows' results, in the order of the list it tested: 0 hidden from the sun, 1 lit, 2 in no cascade, 3 in front
        // of a cascade's near plane. a piece hidden from the camera is put back first and hidden again after, so both
        // states combine (hidden from both: rendering off)
        internal static void SunResults(Unity.Collections.NativeArray<Vector4> data, int count, int listVersion)
        {
            if (listVersion != _listVersion || count != _count) return;
            bool mayHide = HidingNow && _sunPart;
            int hiddenResults = 0;
            for (int i = 0; i < count && i < data.Length; i++)
            {
                var entry = Entries[i];
                if (entry.Batched || entry.Gone) continue;
                // a lamp or fire casts shadows from it now: it keeps them whatever the sun sees
                if (Lit(entry))
                {
                    entry.SunStreak = 0;
                    if (entry.SunHidden) SunShow(entry);
                    continue;
                }
                if ((int)data[i].x == 0)
                {
                    hiddenResults++;
                    if (mayHide && ++entry.SunStreak >= HideAfter && !entry.SunHidden) SunHide(entry);
                    continue;
                }
                entry.SunStreak = 0;
                if (entry.SunHidden) SunShow(entry);
            }
            _sunTests++;
            if (_loggedSunFirst) return;
            _loggedSunFirst = true;
            Plugin.Log.LogInfo($"SunShadowCulling: first test done: {count} pieces, {hiddenResults} hidden from the sun, first time this session");
        }

        private static void SunHide(Entry entry)
        {
            bool cam = entry.Hidden;
            if (cam) Show(entry);
            for (int k = 0; k < entry.Renderers.Length; k++)
            {
                var renderer = entry.Renderers[k];
                if (renderer == null || renderer.forceRenderingOff) continue;
                var mode = renderer.shadowCastingMode;
                if (mode == ShadowCastingMode.ShadowsOnly)
                {
                    renderer.forceRenderingOff = true;
                    entry.SunChanged[k] = 4;
                }
                else if (mode != ShadowCastingMode.Off)
                {
                    entry.SunModes[k] = mode;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    entry.SunChanged[k] = 3;
                }
            }
            entry.SunHidden = true;
            SunHiddenCount++;
            _sunHides++;
            if (cam) Hide(entry);
            if (_loggedSunHide) return;
            _loggedSunHide = true;
            Plugin.Log.LogInfo($"SunShadowCulling: {Label(entry)} at {entry.Bounds.center} casts no shadow, the sun can not see it, first time this session");
        }

        // a piece's name, or the merged mesh's
        private static string Label(Entry entry)
        {
            if (entry.Piece != null) return entry.Piece.name;
            var renderer = entry.Renderers.Length > 0 ? entry.Renderers[0] : null;
            return renderer != null ? renderer.name : "gone";
        }

        // only what the sun test changed is put back, and only where it is still as it left it
        private static void SunShow(Entry entry)
        {
            bool cam = entry.Hidden;
            if (cam) Show(entry);
            for (int k = 0; k < entry.Renderers.Length; k++)
            {
                var renderer = entry.Renderers[k];
                byte changed = entry.SunChanged[k];
                entry.SunChanged[k] = 0;
                if (renderer == null || changed == 0) continue;
                if (changed == 4 && renderer.forceRenderingOff) renderer.forceRenderingOff = false;
                else if (changed == 3 && renderer.shadowCastingMode == ShadowCastingMode.Off) renderer.shadowCastingMode = entry.SunModes[k];
            }
            entry.SunHidden = false;
            entry.SunStreak = 0;
            SunHiddenCount--;
            _sunShows++;
            if (cam) Hide(entry);
        }

        // PieceBatching merges a piece (true: everything this changed goes back, both tests leave it alone) or lets it go
        internal static void SetBatched(Piece piece, bool batched)
        {
            if (piece == null || !Cache.TryGetValue(piece, out var entry)) return;
            if (batched)
            {
                if (entry.SunHidden) SunShow(entry);
                if (entry.Hidden) Show(entry);
            }
            entry.Batched = batched;
            entry.Streak = 0;
            entry.SunStreak = 0;
        }

        internal static void SunShowAll()
        {
            foreach (var entry in Cache.Values)
                if (entry.SunHidden) SunShow(entry);
            foreach (var entry in BatchCache.Values)
                if (entry.SunHidden) SunShow(entry);
            foreach (var entry in Entries) entry.SunStreak = 0;
            SunHiddenCount = 0;
        }

        // a merged mesh of PieceBatching is tested like a piece from the next list on
        internal static void AddBatch(Renderer renderer)
        {
            if (_shader == null || renderer == null || BatchCache.ContainsKey(renderer)) return;
            BatchCache[renderer] = new Entry
            {
                Bounds = renderer.bounds,
                Renderers = new[] { renderer },
                Modes = new ShadowCastingMode[1],
                Changed = new byte[1],
                SunModes = new ShadowCastingMode[1],
                SunChanged = new byte[1],
            };
        }

        // a merged mesh going away: what the tests changed goes back, both skip it until the next list
        internal static void RemoveBatch(Renderer renderer)
        {
            if (ReferenceEquals(renderer, null) || !BatchCache.TryGetValue(renderer, out var entry)) return;
            if (entry.SunHidden) SunShow(entry);
            if (entry.Hidden) Show(entry);
            entry.Gone = true;
            BatchCache.Remove(renderer);
        }

        // right before the main camera culls: the lamps and fires casting shadows now. one that started casting (or moved or
        // grew) gives the pieces in its range their sun shadow back before this frame draws
        private static void OnPreCull(Camera cam)
        {
            if (_shader == null || cam != Utils.GetMainCamera()) return;
            LightsNow.Clear();
            foreach (var light in LocalLights)
            {
                if (light == null || !light.isActiveAndEnabled || light.shadows == LightShadows.None || light.range <= 0f || light.shadowStrength <= 0f) continue;
                var p = light.transform.position;
                LightsNow.Add(new Vector4(p.x, p.y, p.z, light.range));
            }
            bool changed = LightsNow.Count != ShadowLights.Count;
            foreach (var now in LightsNow)
            {
                if (Known(now)) continue;
                changed = true;
                if (SunHiddenCount == 0) continue;
                foreach (var entry in Entries)
                    if (entry.SunHidden && !entry.Gone && InRange(entry.Bounds, now)) SunShow(entry);
            }
            if (!changed) return;
            ShadowLights.Clear();
            ShadowLights.AddRange(LightsNow);
            _lightVersion++;
        }

        // a light casting shadows at the last change, at about the same place with about the same range
        private static bool Known(Vector4 now)
        {
            foreach (var old in ShadowLights)
                if (Mathf.Abs(now.x - old.x) <= LitMargin && Mathf.Abs(now.y - old.y) <= LitMargin && Mathf.Abs(now.z - old.z) <= LitMargin && now.w <= old.w + LitMargin)
                    return true;
            return false;
        }

        // inside the range of a light casting shadows now (read again after every change of the lights)
        private static bool Lit(Entry entry)
        {
            if (entry.LitVersion == _lightVersion) return entry.Lit;
            entry.LitVersion = _lightVersion;
            entry.Lit = false;
            foreach (var light in ShadowLights)
                if (InRange(entry.Bounds, light))
                {
                    entry.Lit = true;
                    break;
                }
            return entry.Lit;
        }

        // the box reaches into the light's range sphere (plus the margin)
        private static bool InRange(Bounds bounds, Vector4 light)
        {
            var c = bounds.center;
            var e = bounds.extents;
            float dx = Mathf.Max(Mathf.Abs(light.x - c.x) - e.x, 0f);
            float dy = Mathf.Max(Mathf.Abs(light.y - c.y) - e.y, 0f);
            float dz = Mathf.Max(Mathf.Abs(light.z - c.z) - e.z, 0f);
            float r = light.w + LitMargin;
            return dx * dx + dy * dy + dz * dz <= r * r;
        }

        private static void OnPostRender(Camera cam)
        {
            if (_shader == null || _count == 0 || cam != Utils.GetMainCamera()) return;
            // the benchmarks' baseline: no part on, no depth test at all
            if (!_cameraPart && !_sunPart && !HiddenLights.PartOn) return;
            if (++_frame % TestEveryFrames != 0 && _snapshotFolder == null) return;
            if (_pending) return;
            try
            {
                Watch.Restart();
                Dispatch(cam);
                _dispatchMs += Watch.Elapsed.TotalMilliseconds;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: the depth test failed and stops, everything put back ({e.GetType().Name}: {e.Message})");
                Camera.onPostRender -= OnPostRender;
                Camera.onPreCull -= OnPreCull;
                if (SunHiddenCount > 0) SunShowAll();
                if (HiddenCount > 0) ShowAll();
                _shader = null;
            }
        }

        private static void Dispatch(Camera cam)
        {
            bool raw = (cam.depthTextureMode & DepthTextureMode.Depth) != 0 || cam.actualRenderingPath == RenderingPath.DeferredShading;
            string srcName = raw ? "_CameraDepthTexture" : "_CameraDepthNormalsTexture";
            var src = Shader.GetGlobalTexture(srcName);
            if (src == null)
            {
                raw = !raw;
                srcName = raw ? "_CameraDepthTexture" : "_CameraDepthNormalsTexture";
                src = Shader.GetGlobalTexture(srcName);
            }
            if (!_loggedSource)
            {
                _loggedSource = true;
                Plugin.Log.LogInfo($"{Name}: {CameraText(cam, srcName, src)}");
            }
            if (src == null) return;
            if (src.width != _gpu.SrcW || src.height != _gpu.SrcH || _gpu.Pyramid == null) _gpu.Size(src.width, src.height);

            float near = cam.nearClipPlane, far = cam.farClipPlane;
            var worldToCamera = cam.worldToCameraMatrix;
            var worldToClip = cam.projectionMatrix * worldToCamera;
            float inflate = Mathf.Max(MinInflate, _speed * InflateSeconds);
            _cmd.Clear();
            _gpu.Record(_cmd, src, raw, OcclusionGpu.ZParams(near, far, SystemInfo.usesReversedZBuffer), near, far, IgnoreNear,
                worldToClip, worldToCamera, _bounds, _count, Margin, MarginScale, inflate, 0, _result, _rect);
            HiddenLights.Record(_cmd, _gpu, cam, worldToClip, worldToCamera, inflate);
            if (_sunPart) SunShadows.Record(_cmd, _bounds, _count, _listVersion);
            Graphics.ExecuteCommandBuffer(_cmd);

            _pending = true;
            _sentList = _listVersion;
            _sentFrame = Time.frameCount;
            _sentPos = cam.transform.position;
            _sentRot = cam.transform.rotation;
            int count = _count;
            AsyncGPUReadback.Request(_result, count * 16, 0, r => Arrived(r, count));
            if (_snapshotFolder != null) StartSnapshot(cam, srcName, src, worldToClip, worldToCamera, count);
        }

        private static void Arrived(AsyncGPUReadbackRequest request, int count)
        {
            _pending = false;
            // the shadow benchmark holds the hidden pieces as they are while it runs
            if (request.hasError) return;
            if (_sentList != _listVersion || Time.frameCount - _sentFrame > MaxResultAge || count != _count)
            {
                _dropped++;
                return;
            }
            Watch.Restart();
            var data = request.GetData<Vector4>();
            var cam = Utils.GetMainCamera();
            // hidden results only count while the camera is still about where the test saw it, and not right after a jump
            bool fresh = cam != null && Vector3.Distance(cam.transform.position, _sentPos) <= StaleMeters
                && Quaternion.Angle(cam.transform.rotation, _sentRot) <= StaleDegrees;
            bool mayHide = _hiding && _cameraPart && fresh && Time.time >= _pauseUntil;
            if (!fresh) _staleTests++;
            int inView = 0, hiddenResults = 0;
            for (int i = 0; i < count && i < data.Length; i++)
            {
                var entry = Entries[i];
                if (entry.Batched || entry.Gone) continue;
                int state = (int)data[i].x;
                if (state == 0)
                {
                    inView++;
                    hiddenResults++;
                    if (!mayHide) continue;
                    if (++entry.Streak >= HideAfter && !entry.Hidden) Hide(entry);
                    continue;
                }
                if (state == 1) inView++;
                entry.Streak = 0;
                if (entry.Hidden) Show(entry);
            }
            _applyMs += Watch.Elapsed.TotalMilliseconds;
            _lastInView = inView;
            _lastHiddenResults = hiddenResults;
            _tests++;
            _testedSum += count;
            _inViewSum += inView;
            _hiddenResultSum += hiddenResults;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Plugin.Log.LogInfo($"{Name}: first depth test done: {count} pieces, in view {inView}, hidden by the walls {hiddenResults}, first time this session");
            }
            if (Time.time >= _nextStat)
            {
                _nextStat = Time.time + StatSeconds;
                if (_tests > 0)
                {
                    _lastSummary = $"{_tests} tests, on average {_testedSum / _tests} pieces, in view {_inViewSum / _tests}, hidden by the walls {_hiddenResultSum / _tests}; " +
                                   $"{HiddenCount} hidden now, {_hides} hides and {_shows} shows, {_jumps} camera jumps, {_staleTests} tests too old to hide by, {_dropped} dropped; " +
                                   $"cpu {_dispatchMs / _tests:0.000} ms to send and {_applyMs / _tests:0.000} ms to apply a test; " +
                                   $"sun: {_sunTests} tests, {SunHiddenCount} without shadow now, {_sunHides} hides and {_sunShows} shows; lights dark now {HiddenLights.HiddenCount}";
                    _sunTests = _sunHides = _sunShows = 0;
                    if (_logLines.Value) Plugin.Log.LogInfo($"{Name}: {(_hiding ? "hiding" : _mode.Value == Mode.On ? "hiding switched off" : "look only")}, {_lastSummary}");
                }
                _tests = _testedSum = _inViewSum = _hiddenResultSum = _hides = _shows = _jumps = _dropped = _staleTests = 0;
                _dispatchMs = _applyMs = 0;
            }
        }

        private static void StartSnapshot(Camera cam, string srcName, Texture src, Matrix4x4 worldToClip, Matrix4x4 worldToCamera, int count)
        {
            var pieces = new List<Piece>(count);
            var items = new List<Entry>(count);
            for (int i = 0; i < count; i++)
            {
                pieces.Add(Entries[i].Piece);
                items.Add(Entries[i]);
            }
            var snap = new Snap
            {
                Folder = _snapshotFolder,
                Pieces = pieces,
                Items = items,
                WorldToClip = worldToClip,
                WorldToCamera = worldToCamera,
                Camera = CameraText(cam, srcName, src) + $", hiding {_hiding}, hidden now {HiddenCount}",
                Waiting = 3,
            };
            _snapshotFolder = null;
            // the first pyramid level at most SnapshotLevelWidth wide
            int level = 0;
            while (level < _gpu.Levels - 1 && _gpu.LevelInfo[level].y > SnapshotLevelWidth) level++;
            snap.LevelIndex = level;
            snap.LevelW = (int)_gpu.LevelInfo[level].y;
            snap.LevelH = (int)_gpu.LevelInfo[level].z;
            AsyncGPUReadback.Request(_result, count * 16, 0, r => { if (!r.hasError) snap.Result = r.GetData<Vector4>().ToArray(); Done(snap); });
            AsyncGPUReadback.Request(_rect, count * 16, 0, r => { if (!r.hasError) snap.Rect = r.GetData<Vector4>().ToArray(); Done(snap); });
            AsyncGPUReadback.Request(_gpu.Pyramid, snap.LevelW * snap.LevelH * 4, (int)_gpu.LevelInfo[level].x * 4,
                r => { if (!r.hasError) snap.Level = r.GetData<float>().ToArray(); Done(snap); });
        }

        private static void Done(Snap snap)
        {
            if (--snap.Waiting > 0) return;
            try
            {
                File.WriteAllText(Path.Combine(snap.Folder, "camera.txt"),
                    snap.Camera + "\nworld to clip\n" + snap.WorldToClip + "\nworld to camera\n" + snap.WorldToCamera +
                    $"\nlevel 0 size {_gpu.LevelInfo[0].y} x {_gpu.LevelInfo[0].z}, snapshot level {snap.LevelIndex} size {snap.LevelW} x {snap.LevelH}\n");
                var csv = new StringBuilder("index,name,x,y,z,state0,near,occ0,level0,x0,y0,x1,y1,hidden\n");
                for (int i = 0; i < snap.Pieces.Count; i++)
                {
                    var piece = snap.Pieces[i];
                    // a merged mesh has no piece: its name, the middle of its box and its own state
                    var item = snap.Items[i];
                    bool batch = ReferenceEquals(item.Piece, null);
                    var p = piece != null ? piece.transform.position : batch ? item.Bounds.center : Vector3.zero;
                    var a = snap.Result != null ? snap.Result[i] : Vector4.zero;
                    var r0 = snap.Rect != null ? snap.Rect[i] : Vector4.zero;
                    bool hidden = batch ? item.Hidden : piece != null && Cache.TryGetValue(piece, out var entry) && entry.Hidden;
                    csv.Append(FormattableString.Invariant($"{i},{(piece != null ? piece.name : batch ? Label(item) : "gone")},{p.x:0.00},{p.y:0.00},{p.z:0.00},{(int)a.x},{a.y:0.00},{a.z:0.00},{(int)a.w},{r0.x:0.0},{r0.y:0.0},{r0.z:0.0},{r0.w:0.0},{(hidden ? 1 : 0)}\n"));
                }
                File.WriteAllText(Path.Combine(snap.Folder, "pieces.csv"), csv.ToString());
                if (snap.Level != null)
                {
                    var bytes = new byte[snap.Level.Length * 4];
                    Buffer.BlockCopy(snap.Level, 0, bytes, 0, bytes.Length);
                    File.WriteAllBytes(Path.Combine(snap.Folder, $"pyramid_level{snap.LevelIndex}_{snap.LevelW}x{snap.LevelH}.f32"), bytes);
                }
                Plugin.Log.LogInfo($"{Name}: snapshot written to {snap.Folder}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: snapshot not written ({e.GetType().Name}: {e.Message})");
            }
        }

        private static string CameraText(Camera cam, string srcName, Texture src) =>
            $"depth from {srcName} {(src != null ? $"{src.width}x{src.height} {src.graphicsFormat}" : "missing")}, render path {cam.actualRenderingPath}, " +
            $"depth modes {cam.depthTextureMode}, reversed z {SystemInfo.usesReversedZBuffer}, screen {cam.pixelWidth}x{cam.pixelHeight}, " +
            $"render target {(cam.targetTexture != null ? $"{cam.targetTexture.width}x{cam.targetTexture.height}" : "none")}, msaa {cam.allowMSAA}, " +
            $"fov {cam.fieldOfView:0.#}, near {cam.nearClipPlane:0.###}, far {cam.farClipPlane:0}, position {cam.transform.position}, graphics {SystemInfo.graphicsDeviceType}";

        private static void Release(ref ComputeBuffer buffer)
        {
            buffer?.Release();
            buffer = null;
        }

        private sealed class SameObject : IEqualityComparer<Piece>
        {
            public bool Equals(Piece a, Piece b) => ReferenceEquals(a, b);
            public int GetHashCode(Piece p) => RuntimeHelpers.GetHashCode(p);
        }

        private sealed class SameRenderer : IEqualityComparer<Renderer>
        {
            public bool Equals(Renderer a, Renderer b) => ReferenceEquals(a, b);
            public int GetHashCode(Renderer r) => RuntimeHelpers.GetHashCode(r);
        }
    }
}
