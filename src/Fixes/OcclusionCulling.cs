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
        // hidden results in a row before a piece is hidden
        private const int HideAfter = 3;
        // results older than this many frames are dropped, hidden results are ignored when the camera moved or turned
        // this much since the test
        private const int MaxResultAge = 8;
        private const float StaleMeters = 0.75f;
        private const float StaleDegrees = 8f;
        // a camera jump (teleport, respawn, loading) or a fast turn between two frames puts everything back for a moment
        private const float JumpMeters = 5f;
        private const float JumpDegrees = 30f;
        private const float PauseSeconds = 0.5f;
        private const float ListSeconds = 1f;
        private const int NewPiecesPerFrame = 400;
        private const int TestEveryFrames = 2;
        private const float StatSeconds = 30f;
        private const int MaxCached = 30000;
        // the plain log line period
        private const float LineSeconds = 5f;
        // the pyramid level written with a snapshot is the first one at most this wide
        private const int SnapshotLevelWidth = 200;

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
        }

        private static readonly Dictionary<Piece, Entry> Cache = new Dictionary<Piece, Entry>(new SameObject());
        private static readonly HashSet<Piece> Skipped = new HashSet<Piece>(new SameObject());
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<Vector4> BoundsData = new List<Vector4>();
        private static readonly List<Piece> Stale = new List<Piece>();
        private static int _count, _listVersion, _frame;
        private static float _nextList;

        // the pieces hidden right now (the probe reads it for its per frame csv)
        public static int HiddenCount;

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
            public Matrix4x4 WorldToClip, WorldToCamera;
            public string Camera;
            public Vector4[] Result, Rect;
            public float[] Level;
            public int LevelIndex, LevelW, LevelH, Waiting;
        }

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _mode = config.Bind("Fixes", Name, Mode.On,
                "Newer, still being tested. On takes building pieces the walls fully hide (by the camera's own depth image) out of the camera's view, their shadows stay. Checked on DirectX 11, other graphics APIs stay off unless ForceOnUntestedVersions. LookOnly tests and logs without hiding anything. Off turns it off.");
            _key = config.Bind("Fixes", Name + "Key", new KeyboardShortcut(KeyCode.Home),
                "With OcclusionCulling = On: switches the hiding off and on while playing, to compare.");
            _benchKey = config.Bind("Fixes", Name + "BenchmarkKey", new KeyboardShortcut(KeyCode.Home, KeyCode.LeftShift),
                "With OcclusionCulling = On: stand still and press it, the hiding switches on and off every 2 s for 20 s and the log gets the frame time, draw calls and triangles of both.");
            _logLines = config.Bind("Fixes", Name + "LogLines", false,
                "For testing: a log line every 5 s while it runs (where, how many pieces are hidden, frame and render time, draw calls) and a summary of the depth tests every 30 s.");
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
                bundle.Unload(false);
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
            if (_mode.Value == Mode.On && !Typing())
            {
                if (_benchKey.Value.IsDown()) Benchmark.Begin(Name, "hiding on", "off", SetHiding, _hiding, () => HiddenCount, "hidden");
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
            if (Time.time < _nextList) return;
            _nextList = Time.time + ListSeconds;
            RebuildList();
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

        private static void RebuildList()
        {
            var all = _allPieces();
            if (all == null) return;
            _listVersion++;
            Entries.Clear();
            BoundsData.Clear();
            int fresh = 0;
            foreach (var piece in all)
            {
                if (piece == null) continue;
                if (!Cache.TryGetValue(piece, out var entry))
                {
                    if (Skipped.Contains(piece) || fresh >= NewPiecesPerFrame) continue;
                    fresh++;
                    entry = Read(piece);
                    if (entry == null)
                    {
                        Skipped.Add(piece);
                        continue;
                    }
                    if (Cache.Count >= MaxCached && HiddenCount == 0)
                    {
                        Cache.Clear();
                        Skipped.Clear();
                    }
                    Cache[piece] = entry;
                }
                entry.SeenList = _listVersion;
                Entries.Add(entry);
                BoundsData.Add(entry.Bounds.center);
                BoundsData.Add(entry.Bounds.extents);
            }
            // pieces gone from the game's list: put back if still there (a hidden piece must never stay hidden), forget them
            Stale.Clear();
            foreach (var pair in Cache)
                if (pair.Value.SeenList != _listVersion) Stale.Add(pair.Key);
            foreach (var piece in Stale)
            {
                var entry = Cache[piece];
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
        // (ships, carts), with moving parts (doors, anything animated), or without a mesh
        private static Entry Read(Piece piece)
        {
            var view = piece.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return null;
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
            };
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
                Plugin.Log.LogInfo($"{Name}: hid {entry.Piece.name} at {entry.Bounds.center} behind the walls, first time this session");
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
            foreach (var entry in Entries) entry.Streak = 0;
            if (HiddenCount != 0)
            {
                Plugin.Log.LogWarning($"{Name}: {HiddenCount} pieces counted hidden after putting everything back, count reset");
                HiddenCount = 0;
            }
        }

        private static void OnPostRender(Camera cam)
        {
            if (_shader == null || _count == 0 || cam != Utils.GetMainCamera()) return;
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
            bool mayHide = _hiding && fresh && Time.time >= _pauseUntil;
            if (!fresh) _staleTests++;
            int inView = 0, hiddenResults = 0;
            for (int i = 0; i < count && i < data.Length; i++)
            {
                var entry = Entries[i];
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
                                   $"cpu {_dispatchMs / _tests:0.000} ms to send and {_applyMs / _tests:0.000} ms to apply a test";
                    if (_logLines.Value) Plugin.Log.LogInfo($"{Name}: {(_hiding ? "hiding" : _mode.Value == Mode.On ? "hiding switched off" : "look only")}, {_lastSummary}");
                }
                _tests = _testedSum = _inViewSum = _hiddenResultSum = _hides = _shows = _jumps = _dropped = _staleTests = 0;
                _dispatchMs = _applyMs = 0;
            }
        }

        private static void StartSnapshot(Camera cam, string srcName, Texture src, Matrix4x4 worldToClip, Matrix4x4 worldToCamera, int count)
        {
            var pieces = new List<Piece>(count);
            for (int i = 0; i < count; i++) pieces.Add(Entries[i].Piece);
            var snap = new Snap
            {
                Folder = _snapshotFolder,
                Pieces = pieces,
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
                    var p = piece != null ? piece.transform.position : Vector3.zero;
                    var a = snap.Result != null ? snap.Result[i] : Vector4.zero;
                    var r0 = snap.Rect != null ? snap.Rect[i] : Vector4.zero;
                    bool hidden = piece != null && Cache.TryGetValue(piece, out var entry) && entry.Hidden;
                    csv.Append(FormattableString.Invariant($"{i},{(piece != null ? piece.name : "gone")},{p.x:0.00},{p.y:0.00},{p.z:0.00},{(int)a.x},{a.y:0.00},{a.z:0.00},{(int)a.w},{r0.x:0.0},{r0.y:0.0},{r0.z:0.0},{r0.w:0.0},{(hidden ? 1 : 0)}\n"));
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
    }
}
