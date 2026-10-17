using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace GapCloser.Fixes
{
    // a support view for the big mine rocks (copper, silver and other deposits made of parts): every part of the rock you
    // look at or hit gets a soft tint by what breaking does: blue holds other parts (they drop when it breaks), green drops
    // (it hangs on a blue part, or is loose already), red is held from outside, yellow is held by other parts, and the
    // hover text gives the numbers. a hotkey hides the tint while playing. it works out the game's own support rule
    // (RockSupportModel) from the same boxes and the same physics query the game uses, locally, sends nothing and changes
    // nothing in the world. an optional self-check compares it with the game's own check and logs any difference
    internal static class RockSupport
    {
        private const string Name = "RockSupport";
        private const string Section = Sections.RockSupport;
        private const string Command = "gapcloser_rocksupport";
        private const string BundleResource = "GapCloser.gapcloser_rocksupport";
        // how far the rock may be and how long it stays shown after you last looked at it or hit it
        private const float MaxDistance = 40f;
        private const float ShowSeconds = 60f;
        private const float RefreshSeconds = 0.3f;
        private const float SelfCheckSeconds = 2f;
        private const float StatSeconds = 60f;
        // the game's overlap buffer holds 128 colliders (MineRock5.m_tempColliders)
        private const int QueryBuffer = 128;

        private static ConfigEntry<bool> _enabled, _showAnchors, _selfCheck, _logChecks;
        private static ConfigEntry<Color> _fallsColor, _bestColor, _anchorColor, _heldColor;
        private static ConfigEntry<KeyboardShortcut> _toggleKey;
        private static bool _on;
        // the tint is shown at every game start, the hotkey hides it until pressed again
        private static bool _show = true;

        // the game's private parts of MineRock5
        private static AccessTools.FieldRef<MineRock5, bool> _haveSetupBounds;
        private static AccessTools.FieldRef<MineRock5, ZNetView> _nview;
        private static FieldInfo _hitAreasField;
        private static AccessTools.FieldRef<object, Collider> _collider;
        private static AccessTools.FieldRef<object, MeshFilter> _meshFilter;
        private static AccessTools.FieldRef<object, float> _health;
        private static AccessTools.FieldRef<object, bool> _supported;
        private delegate void BoundReader(object area, out Vector3 pos, out Vector3 size, out Quaternion rot);
        private static BoundReader _readBound;
        private static AccessTools.FieldRef<int> _rayMask, _groundLayer;
        private static Action<MineRock5> _setupColliders, _updateSupport;

        // drawing
        private static Material _tint;
        private static MaterialPropertyBlock _fallsBlock, _bestBlock, _anchorBlock, _heldBlock;

        // the rock in focus and its worked out support
        private static MineRock5 _rock;
        private static float _lastSeen, _nextRefresh, _nextCheck, _nextStat;
        private static uint _lastRevision;
        private static readonly Collider[] Buffer = new Collider[QueryBuffer];
        private static readonly List<object> Areas = new List<object>();
        private static readonly Dictionary<Collider, int> IndexOf = new Dictionary<Collider, int>();
        private static readonly List<int> Touching = new List<int>();
        private static bool[] _outside = new bool[0], _alive = new bool[0], _now = new bool[0], _scratch = new bool[0];
        private static int[][] _own = new int[0][];
        private static int[] _falls = new int[0], _holds = new int[0];
        private static bool[] _hangs = new bool[0];

        // what breaking a part does, its tint: drops green, holds others blue, held from outside red, held by others yellow
        private enum Kind { Dead, Drops, Holds, Outside, Held }
        private static int _fallsNow, _best, _bestFalls, _anchors, _aliveCount, _hovered = -1, _fullQueries;
        private static string _hoverBase, _hoverText;
        private static int _hoverFor = int.MinValue;

        // stats
        private static int _refreshes, _checks, _compared, _mismatches;
        private static bool _loggedFirst, _loggedMismatch, _loggedFull;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _enabled = config.Bind(Section, "Enabled", true,
                $"Tints each part of the mine rock you look at by what breaking it does: blue holds others, green drops, red is held from outside, yellow by other parts. {Command} switches it.");
            _toggleKey = config.Bind(Section, "ToggleKey", new KeyboardShortcut(KeyCode.O, KeyCode.LeftShift),
                "Hides or shows the tint while playing, shown again at every start.");
            _showAnchors = config.Bind(Section, "ShowAnchors", true, "Also tints the parts whose loss drops nothing (red and yellow).");
            _fallsColor = config.Bind(Section, "FallsColor", new Color(0.18f, 0.58f, 0.22f, 0.20f), "Tint of the parts that drop.");
            _bestColor = config.Bind(Section, "BestColor", new Color(0.16f, 0.38f, 0.82f, 0.24f), "Tint of the parts that hold others.");
            _anchorColor = config.Bind(Section, "AnchorColor", new Color(0.68f, 0.16f, 0.12f, 0.20f), "Tint of the parts held from outside.");
            _heldColor = config.Bind(Section, "HeldColor", new Color(0.76f, 0.60f, 0.12f, 0.20f), "Tint of the parts held only by other parts.");
            _selfCheck = config.Bind(Sections.Debug, "RockSupportSelfCheck", false,
                "Test: compares the rock view with the game's own check every few seconds and logs any difference.");
            _logChecks = config.Bind(Sections.Debug, "RockSupportLogChecks", false,
                "Test: a log line for each hit that breaks a rock part.");
            if (!_enabled.Value || !Plugin.GameMatches(Name)) return;
            if (!Bind())
            {
                Plugin.Log.LogWarning($"{Name}: the game's mine rock parts did not look as checked, off");
                return;
            }
            LoadTint();
            // a prefix: on the rock's owner the hit's damage and support check run inside Damage, the state before it is needed
            harmony.Patch(AccessTools.Method(typeof(MineRock5), nameof(MineRock5.Damage)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(RockSupport), nameof(Damaged))));
            harmony.Patch(AccessTools.Method(typeof(MineRock5), nameof(MineRock5.GetHoverText)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(RockSupport), nameof(HoverText))));
            harmony.Patch(AccessTools.Method(typeof(Terminal), "InitTerminal"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(RockSupport), nameof(Register))));
            _on = true;
            Plugin.Log.LogInfo($"{Name}: on, tint {(_tint != null ? $"ready, {_toggleKey.Value} hides it" : "missing, hover text only")}, self-check {(_selfCheck.Value ? "on" : "off")}, console command {Command}");
        }

        private static bool Bind()
        {
            try
            {
                var area = AccessTools.TypeByName("MineRock5+HitArea");
                var bound = AccessTools.TypeByName("MineRock5+BoundData");
                if (area == null || bound == null) return false;
                _hitAreasField = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
                var boundField = AccessTools.Field(area, "m_bound");
                var pos = AccessTools.Field(bound, "m_pos");
                var size = AccessTools.Field(bound, "m_size");
                var rot = AccessTools.Field(bound, "m_rot");
                var rayMask = AccessTools.Field(typeof(MineRock5), "m_rayMask");
                var groundLayer = AccessTools.Field(typeof(MineRock5), "m_groundLayer");
                var setup = AccessTools.Method(typeof(MineRock5), "SetupColliders");
                var update = AccessTools.Method(typeof(MineRock5), "UpdateSupport");
                if (_hitAreasField == null || boundField == null || pos == null || size == null || rot == null || rayMask == null || groundLayer == null || setup == null || update == null)
                    return false;
                _haveSetupBounds = AccessTools.FieldRefAccess<MineRock5, bool>("m_haveSetupBounds");
                _nview = AccessTools.FieldRefAccess<MineRock5, ZNetView>("m_nview");
                _collider = AccessTools.FieldRefAccess<Collider>(area, "m_collider");
                _meshFilter = AccessTools.FieldRefAccess<MeshFilter>(area, "m_meshFilter");
                _health = AccessTools.FieldRefAccess<float>(area, "m_health");
                _supported = AccessTools.FieldRefAccess<bool>(area, "m_supported");
                _rayMask = AccessTools.StaticFieldRefAccess<int>(rayMask);
                _groundLayer = AccessTools.StaticFieldRefAccess<int>(groundLayer);
                _setupColliders = AccessTools.MethodDelegate<Action<MineRock5>>(setup);
                _updateSupport = AccessTools.MethodDelegate<Action<MineRock5>>(update);
                // the stored box of a part, read without boxing the private struct
                var dm = new DynamicMethod("GapCloserReadBound", typeof(void),
                    new[] { typeof(object), typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(Quaternion).MakeByRefType() },
                    typeof(RockSupport).Module, true);
                var il = dm.GetILGenerator();
                foreach (var (arg, field, type) in new[] { (1, pos, typeof(Vector3)), (2, size, typeof(Vector3)), (3, rot, typeof(Quaternion)) })
                {
                    il.Emit(arg == 1 ? OpCodes.Ldarg_1 : arg == 2 ? OpCodes.Ldarg_2 : OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, area);
                    il.Emit(OpCodes.Ldflda, boundField);
                    il.Emit(OpCodes.Ldfld, field);
                    il.Emit(OpCodes.Stobj, type);
                }
                il.Emit(OpCodes.Ret);
                _readBound = (BoundReader)dm.CreateDelegate(typeof(BoundReader));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: binding failed ({e.GetType().Name}: {e.Message})");
                return false;
            }
        }

        private static void LoadTint()
        {
            Shader shader = null;
            try
            {
                using (var stream = typeof(RockSupport).Assembly.GetManifestResourceStream(BundleResource))
                {
                    if (stream != null)
                    {
                        var bytes = new byte[stream.Length];
                        int read = 0;
                        while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                        var bundle = AssetBundle.LoadFromMemory(bytes);
                        if (bundle != null)
                        {
                            shader = bundle.LoadAsset<Shader>("GapCloserRockTint");
                            bundle.Unload(false);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: the tint shader did not load ({e.GetType().Name}: {e.Message})");
            }
            if (shader == null || !shader.isSupported) return;
            _tint = new Material(shader) { name = "GapCloser rock tint" };
            _fallsBlock = Block(_fallsColor.Value);
            _bestBlock = Block(_bestColor.Value);
            _anchorBlock = Block(_anchorColor.Value);
            _heldBlock = Block(_heldColor.Value);
        }

        private static MaterialPropertyBlock Block(Color color)
        {
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", color);
            return block;
        }

        private static void Register()
        {
            new Terminal.ConsoleCommand(Command, "switches the mine rock support view on and off", (Terminal.ConsoleEvent)Switch);
        }

        private static void Switch(Terminal.ConsoleEventArgs args)
        {
            _on = !_on;
            if (!_on) Forget();
            args.Context.AddString($"{Command}: {(_on ? "on" : "off")}");
            Plugin.Log.LogInfo($"{Name}: switched {(_on ? "on" : "off")} by {Command}");
        }

        // the local player's hits put the rock in focus; the support as it is before the hit is kept to check the outcome
        private static void Damaged(MineRock5 __instance, HitData hit)
        {
            if (!_on || __instance == null || hit == null || Player.m_localPlayer == null || hit.GetAttacker() != Player.m_localPlayer) return;
            try
            {
                Focus(__instance);
                Refresh(__instance);
                if (_pending != null) Resolve(_pending, true);
                _pending = new Pending
                {
                    Rock = __instance,
                    Parts = Areas.ToArray(),
                    Alive = (bool[])_alive.Clone(),
                    Outside = (bool[])_outside.Clone(),
                    Own = (int[][])_own.Clone(),
                    Hit = HitParts(__instance, hit),
                    At = Time.time,
                };
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: could not keep the state before a hit ({e.GetType().Name}: {e.Message})");
                _pending = null;
            }
            _nextRefresh = Time.time + 0.15f;
            _nextCheck = Math.Min(_nextCheck, Time.time + 0.5f);
        }

        // the parts the hit damages, chosen like MineRock5.Damage: the hit collider's part, or with a radius (or no hit
        // collider) every part whose collider (or its parent) hangs under the rock within the sphere
        private static bool[] HitParts(MineRock5 rock, HitData hit)
        {
            var parts = new bool[Areas.Count];
            if (hit.m_hitCollider == null || hit.m_radius > 0f)
            {
                int found = Physics.OverlapSphereNonAlloc(hit.m_point, hit.m_radius > 0f ? hit.m_radius : 0.05f, Buffer, _rayMask());
                for (int k = 0; k < found; k++)
                {
                    var parent = Buffer[k].transform.parent;
                    if (parent != rock.transform && (parent == null || parent.parent != rock.transform)) continue;
                    if (IndexOf.TryGetValue(Buffer[k], out int i)) parts[i] = true;
                }
            }
            else if (IndexOf.TryGetValue(hit.m_hitCollider, out int i)) parts[i] = true;
            return parts;
        }

        private sealed class Pending
        {
            public MineRock5 Rock;
            // the rock's part objects, read for their health whichever rock is in focus meanwhile
            public object[] Parts;
            public bool[] Alive, Outside, Hit;
            public int[][] Own;
            public float At, ChangedAt;
            public bool Changed;
        }

        private static Pending _pending;
        private const float SettleSeconds = 0.5f;
        private const float GiveUpSeconds = 2.5f;
        private static int _hitsBroke, _matched, _differed, _skipped;

        // watches the rock after a hit: once its parts stopped changing (or after a while) the game's outcome is compared
        // with the replay of the game's rule on the state before the hit
        private static void Watch()
        {
            var p = _pending;
            if (p == null) return;
            // the game removes the whole rock when its last part goes
            if (p.Rock == null)
            {
                Resolve(p, false);
                return;
            }
            if (!p.Changed)
            {
                for (int i = 0; i < p.Alive.Length; i++)
                {
                    if (!p.Alive[i] || _health(p.Parts[i]) > 0f) continue;
                    p.Changed = true;
                    p.ChangedAt = Time.time;
                    break;
                }
            }
            if ((p.Changed && Time.time - p.ChangedAt >= SettleSeconds) || Time.time - p.At >= GiveUpSeconds) Resolve(p, false);
        }

        private static void Resolve(Pending p, bool early)
        {
            _pending = null;
            int n = p.Alive.Length;
            bool rockThere = p.Rock != null;
            var lost = new List<int>();
            var broke = new List<int>();
            for (int i = 0; i < n; i++)
            {
                bool aliveNow = rockThere && _health(p.Parts[i]) > 0f;
                if (!p.Alive[i] || aliveNow) continue;
                lost.Add(i);
                if (p.Hit[i]) broke.Add(i);
            }
            if (lost.Count == 0) return;
            if (early || broke.Count == 0)
            {
                _skipped++;
                if (_logChecks.Value) Plugin.Log.LogInfo($"{Name}: hit on {Label(p.Rock)}: {lost.Count} parts lost {(early ? "while the next hit came" : "with no hit part broken (another player or something else)")}, not compared");
                return;
            }
            _hitsBroke++;
            var dead = new bool[n];
            for (int i = 0; i < n; i++) dead[i] = !p.Alive[i];
            RockSupportModel.Replay(p.Outside, p.Own, dead, broke.ToArray(), new bool[n]);
            var predicted = new List<int>();
            for (int i = 0; i < n; i++)
                if (dead[i] && p.Alive[i] && !broke.Contains(i)) predicted.Add(i);
            var fell = lost.FindAll(i => !broke.Contains(i));
            bool match = predicted.Count == fell.Count && predicted.TrueForAll(fell.Contains);
            if (match) _matched++;
            else _differed++;
            string Join(List<int> list) => list.Count <= 15 ? "[" + string.Join(",", list) + "]" : $"[{string.Join(",", list.GetRange(0, 15))},... {list.Count}]";
            string text = $"{Name}: hit on {Label(p.Rock)} broke {Join(broke)}: predicted to fall {Join(predicted)}, fell {Join(fell)}, {(match ? "MATCH" : "DIFFERENT")}";
            if (!match) Plugin.Log.LogWarning(text + $" (predicted only {Join(predicted.FindAll(i => !fell.Contains(i)))}, fell only {Join(fell.FindAll(i => !predicted.Contains(i)))})");
            else if (_logChecks.Value) Plugin.Log.LogInfo(text);
        }

        private static string Label(MineRock5 rock) => rock != null ? $"{rock.name} at ({rock.transform.position.x:0}, {rock.transform.position.z:0})" : "a rock gone meanwhile";

        // the probe calls this at a marker (by reflection): the rock in focus as text
        public static string Snapshot(string folder)
        {
            if (!_on) return "rock support: off";
            if (_rock == null || Areas.Count == 0) return "rock support: no rock in focus";
            System.IO.Directory.CreateDirectory(folder);
            var text = new StringBuilder();
            text.AppendLine($"rock {Label(_rock)}: {_aliveCount} of {Areas.Count} parts left, {_fallsNow} loose, best part {_best} drops {_bestFalls}, {_anchors} held from outside, hovered part {_hovered}");
            text.AppendLine("part,alive,outside,supported,drops_if_broken,holds,kind,touching");
            for (int i = 0; i < Areas.Count && i < _alive.Length; i++)
                text.AppendLine($"{i},{(_alive[i] ? 1 : 0)},{(_outside[i] ? 1 : 0)},{(_now[i] ? 1 : 0)},{_falls[i]},{_holds[i]},{KindOf(i)},{(_own[i] != null ? string.Join(" ", _own[i]) : "")}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "rock_state.txt"), text.ToString());
            return $"rock support: {Label(_rock)}, {_aliveCount} of {Areas.Count} parts left, {_fallsNow} loose, best part {_best} drops {_bestFalls}, hovered part {_hovered}; this session {_hitsBroke} hits broke parts, {_matched} matched, {_differed} different, {_skipped} not compared";
        }

        private static void Focus(MineRock5 rock)
        {
            if (rock != _rock)
            {
                _rock = rock;
                _nextRefresh = 0f;
                _lastRevision = 0;
            }
            _lastSeen = Time.time;
        }

        private static void Forget()
        {
            _rock = null;
            _hovered = -1;
            _hoverFor = int.MinValue;
            Areas.Clear();
        }

        // every frame: what the crosshair is on, the refresh, the tint
        internal static void Frame()
        {
            if (!_on) return;
            if (_tint != null && _toggleKey.Value.IsDown() && !OcclusionCulling.Typing())
            {
                _show = !_show;
                Plugin.Log.LogInfo($"{Name}: tint {(_show ? "shown" : "hidden")} by {_toggleKey.Value}");
                if (MessageHud.instance != null)
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, $"Rock support tint {(_show ? "on" : "off")}");
            }
            var player = Player.m_localPlayer;
            var cam = Utils.GetMainCamera();
            if (player == null || cam == null)
            {
                if (_rock != null) Forget();
                return;
            }
            _hovered = -1;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (Physics.Raycast(ray, out var hit, MaxDistance, _rayMask(), QueryTriggerInteraction.Ignore) && hit.collider != null)
            {
                var rock = hit.collider.GetComponentInParent<MineRock5>();
                if (rock != null)
                {
                    Focus(rock);
                    if (rock == _rock && IndexOf.TryGetValue(hit.collider, out int index)) _hovered = index;
                }
            }
            Watch();
            if (_rock == null)
            {
                // mined out (the game destroys the rock object) or never focused
                if (Areas.Count > 0) Forget();
                return;
            }
            if (Time.time - _lastSeen > ShowSeconds || Vector3.Distance(player.transform.position, _rock.transform.position) > MaxDistance * 1.5f)
            {
                Forget();
                return;
            }
            var view = _nview(_rock);
            if (view == null || !view.IsValid())
            {
                Forget();
                return;
            }
            uint revision = view.GetZDO().DataRevision;
            if (Time.time >= _nextRefresh || revision != _lastRevision)
            {
                _nextRefresh = Time.time + RefreshSeconds;
                _lastRevision = revision;
                try
                {
                    Refresh(_rock);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"{Name}: refresh failed and the view stops ({e.GetType().Name}: {e.Message})");
                    _on = false;
                    Forget();
                    return;
                }
            }
            Draw(cam);
            Stats();
        }

        // the same boxes, query, skips and support rules as MineRock5.UpdateSupport and GetSupport, gathered once per refresh
        private static void Refresh(MineRock5 rock)
        {
            if (!_haveSetupBounds(rock))
            {
                _setupColliders(rock);
                _haveSetupBounds(rock) = true;
            }
            Areas.Clear();
            IndexOf.Clear();
            if (!(_hitAreasField.GetValue(rock) is IList list)) return;
            foreach (var area in list) Areas.Add(area);
            int n = Areas.Count;
            if (_outside.Length != n)
            {
                _outside = new bool[n];
                _alive = new bool[n];
                _now = new bool[n];
                _scratch = new bool[n];
                _own = new int[n][];
                _falls = new int[n];
                _holds = new int[n];
                _hangs = new bool[n];
            }
            for (int i = 0; i < n; i++)
            {
                var c = _collider(Areas[i]);
                if (c != null && !IndexOf.ContainsKey(c)) IndexOf[c] = i;
                _alive[i] = _health(Areas[i]) > 0f;
            }
            var origin = rock.transform.position;
            int mask = _rayMask(), ground = _groundLayer();
            for (int i = 0; i < n; i++)
            {
                var area = Areas[i];
                _readBound(area, out var pos, out var size, out var rot);
                var self = _collider(area);
                int found = Physics.OverlapBoxNonAlloc(origin + pos, size, Buffer, rot, mask);
                if (found >= QueryBuffer)
                {
                    _fullQueries++;
                    if (!_loggedFull)
                    {
                        _loggedFull = true;
                        Plugin.Log.LogInfo($"{Name}: a part's box touched {QueryBuffer} colliders or more (the game's buffer is full there too), first time this session");
                    }
                }
                bool outside = false;
                Touching.Clear();
                for (int k = 0; k < found; k++)
                {
                    var c = Buffer[k];
                    if (c == self || c.attachedRigidbody != null || c.isTrigger) continue;
                    if (c.gameObject.layer == ground)
                    {
                        outside = true;
                        continue;
                    }
                    var destructible = c.gameObject.GetComponentInParent<IDestructible>();
                    if (destructible == null)
                    {
                        outside = true;
                        continue;
                    }
                    if (ReferenceEquals(destructible, rock) && IndexOf.TryGetValue(c, out int j))
                    {
                        Touching.Add(j);
                        continue;
                    }
                    if (c.transform.position.y < origin.y) outside = true;
                }
                _outside[i] = outside;
                _own[i] = Touching.Count > 0 ? Touching.ToArray() : null;
            }
            RockSupportModel.Run(_outside, _own, -1, _now);
            _fallsNow = RockSupportModel.Falls(_alive, _now, -1);
            RockSupportModel.Classify(_outside, _own, _alive, _now, _scratch, _falls, _holds, _hangs);
            _best = -1;
            _bestFalls = 0;
            _anchors = 0;
            _aliveCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (!_alive[i]) continue;
                _aliveCount++;
                if (_outside[i]) _anchors++;
                if (_falls[i] > _bestFalls)
                {
                    _bestFalls = _falls[i];
                    _best = i;
                }
            }
            _hoverFor = int.MinValue;
            _refreshes++;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Plugin.Log.LogInfo($"{Name}: first rock {rock.name}: {_aliveCount} of {n} parts left, {_fallsNow} fall at the next hit, the best part drops {_bestFalls}, {_anchors} held from outside, first time this session");
            }
            if (_selfCheck.Value && Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + SelfCheckSeconds;
                SelfCheck(rock, n);
            }
        }

        // the game's own check (it only sets the flags; destroying is CheckSupport's job on the rock's owner)
        private static void SelfCheck(MineRock5 rock, int n)
        {
            _updateSupport(rock);
            _checks++;
            int differ = 0, first = -1;
            for (int i = 0; i < n; i++)
            {
                _compared++;
                if (_supported(Areas[i]) == _now[i]) continue;
                differ++;
                if (first < 0) first = i;
            }
            _mismatches += differ;
            if (differ > 0 && !_loggedMismatch)
            {
                _loggedMismatch = true;
                Plugin.Log.LogWarning($"{Name}: {differ} of {n} parts differ from the game's own check on {rock.name} (part {first}: game {_supported(Areas[first])}, ours {_now[first]}, alive {_alive[first]}, outside {_outside[first]}), first time this session");
            }
        }

        private static void Draw(Camera cam)
        {
            if (_tint == null || _rock == null || !_show) return;
            for (int i = 0; i < Areas.Count && i < _alive.Length; i++)
            {
                var kind = KindOf(i);
                var block = kind == Kind.Drops ? _fallsBlock : kind == Kind.Holds ? _bestBlock : !_showAnchors.Value ? null :
                    kind == Kind.Outside ? _anchorBlock : kind == Kind.Held ? _heldBlock : null;
                if (block == null) continue;
                var filter = _meshFilter(Areas[i]);
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                var matrix = filter.transform.localToWorldMatrix;
                for (int s = 0; s < mesh.subMeshCount; s++)
                    Graphics.DrawMesh(mesh, matrix, _tint, 0, cam, s, block, ShadowCastingMode.Off, false);
            }
        }

        // the hover lines, rebuilt only when the rock's numbers or the hovered part change
        private static void HoverText(MineRock5 __instance, ref string __result)
        {
            if (!_on || __instance == null || __instance != _rock || Areas.Count == 0) return;
            int key = _refreshes * 1024 + _hovered;
            if (key != _hoverFor || !ReferenceEquals(__result, _hoverBase))
            {
                _hoverFor = key;
                _hoverBase = __result;
                var text = new StringBuilder(__result);
                text.Append($"\n<size=80%><color=#C9A46A>{_aliveCount} parts left, {_fallsNow} loose (they drop when you break any part)</color>");
                if (_best >= 0) text.Append($"\n<color=#7FB0BC>Best part to break drops {_bestFalls}</color>");
                if (_hovered >= 0 && _hovered < _alive.Length && _alive[_hovered])
                {
                    int h = _hovered;
                    var kind = KindOf(h);
                    string state = kind == Kind.Drops
                        ? (!_now[h] ? "loose, drops when you break any part" : "hangs on a blue part, drops when that breaks") + (_holds[h] > 0 ? $", breaking it drops {_falls[h]}" : "") + " (green)"
                        : kind == Kind.Holds ? $"holds {_holds[h]} other parts, breaking it drops {_falls[h]} (blue)"
                        : kind == Kind.Outside ? "held from outside, breaking it drops nothing (red)"
                        : "held by other parts, breaking it drops nothing (yellow)";
                    text.Append($"\n<color=#A8A8A8>This part: {state}</color>");
                }
                text.Append("</size>");
                _hoverText = text.ToString();
            }
            __result = _hoverText;
        }

        private static Kind KindOf(int i) =>
            !_alive[i] ? Kind.Dead : !_now[i] || _hangs[i] ? Kind.Drops : _holds[i] > 0 ? Kind.Holds : _outside[i] ? Kind.Outside : Kind.Held;

        private static void Stats()
        {
            if (Time.time < _nextStat) return;
            _nextStat = Time.time + StatSeconds;
            if (_refreshes > 0 && _logChecks.Value)
                Plugin.Log.LogInfo($"{Name}: last {StatSeconds:0} s: {_refreshes} refreshes, self-check {_checks} times over {_compared} parts, {_mismatches} differ from the game; {_fullQueries} full queries; this session {_hitsBroke} hits broke parts, {_matched} outcomes matched, {_differed} different, {_skipped} not compared");
            _refreshes = _checks = _compared = _mismatches = _fullQueries = 0;
        }
    }
}
