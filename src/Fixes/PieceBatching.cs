using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace GapCloser.Fixes
{
    // newer, still being tested: building pieces in cells farther than MergeMeters are drawn as a few merged meshes per
    // cell and material instead of one draw (and one shadow caster) per piece. each piece is merged with the level of
    // detail the game would show it at that distance, a piece the game would not draw there stays as it is, and wet,
    // snow and ash parts, lights, effects and anything you can use stay apart. a cell falls back to its own pieces at
    // once when the camera comes near, a piece in it changes its look (damage, repair) or goes away, and merges again
    // later. OcclusionCulling hides the merged meshes behind walls and from the sun like pieces. the idea of merging
    // building pieces per area comes from ValheimBuildOptimization by RockBreaker (Dracoalias)
    internal static class PieceBatching
    {
        private const string Name = "PieceBatching";
        private const float CellSize = 32f;
        private const float MergeMeters = 48f;
        private const float UnmergeMeters = 40f;
        // a merged cell is built again after the camera moved this far since (the level of detail it chose)
        private const float RefreshMeters = 64f;
        // after a change a cell waits this long before it merges again
        private const float CooldownSeconds = 3f;
        private const int MinPieces = 8;
        private const int MaxVertices = 200000;
        private const float ScanSeconds = 1f;
        // reading new pieces, at most this long a frame (at least a few)
        private const float ReadBudgetMs = 1f;
        private const int MinReads = 10;
        // render queue from which a material counts as see-through
        private const int TransparentQueue = 2500;

        private static ConfigEntry<bool> _enabled;
        // the run of all benchmarks switches merged cells between their merged meshes and their own pieces; while it is off
        // nothing merges or comes apart
        private static bool _partOn = true;
        private static AccessTools.FieldRef<List<Piece>> _allPieces;
        private static readonly AccessTools.FieldRef<WearNTear, GameObject> AshRoof = AccessTools.FieldRefAccess<WearNTear, GameObject>("m_ashroof");

        private sealed class Cell
        {
            public Vector3Int Key;
            public Bounds Bounds;
            public readonly List<Piece> Pieces = new List<Piece>();
            public bool Merged;
            public Vector3 BuiltAt;
            public float NotBefore;
            public readonly List<GameObject> Batches = new List<GameObject>();
            public readonly List<Renderer> Off = new List<Renderer>();
            public readonly List<Piece> Released = new List<Piece>();
        }

        private struct GroupKey : IEquatable<GroupKey>
        {
            public Material Material;
            public ShadowCastingMode Shadows;
            public bool Receive;
            public int Layer;
            public LightProbeUsage LightProbes;
            public ReflectionProbeUsage ReflectionProbes;

            public bool Equals(GroupKey o) => ReferenceEquals(Material, o.Material) && Shadows == o.Shadows && Receive == o.Receive && Layer == o.Layer
                && LightProbes == o.LightProbes && ReflectionProbes == o.ReflectionProbes;
            public override bool Equals(object obj) => obj is GroupKey o && Equals(o);
            public override int GetHashCode() => (RuntimeHelpers.GetHashCode(Material) * 397) ^ ((int)Shadows << 1) ^ (Receive ? 1 : 0) ^ (Layer << 8) ^ ((int)LightProbes << 16) ^ ((int)ReflectionProbes << 20);
        }

        private sealed class Group
        {
            public GroupKey Key;
            public readonly List<CombineInstance> Parts = new List<CombineInstance>();
            public int Vertices;
        }

        private static readonly Dictionary<Vector3Int, Cell> Cells = new Dictionary<Vector3Int, Cell>();
        private static readonly Dictionary<Piece, Cell> PieceCell = new Dictionary<Piece, Cell>(new SameObject());
        private static readonly HashSet<Piece> Skipped = new HashSet<Piece>(new SameObject());
        private static readonly List<Cell> Gone = new List<Cell>();
        private static readonly Stopwatch Watch = new Stopwatch();
        private static readonly Predicate<Piece> IsGone = p => p == null;
        private static GameObject _root;
        private static float _nextScan, _nextStat;
        private static int _merges, _unmerges, _mergedPieces, _switches;
        private static double _mergeMs;
        private static bool _loggedMerge, _loggedUnmerge;
        private const float StatSeconds = 60f;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _enabled = config.Bind(Sections.Fixes, Name, true,
                "Building pieces farther than " + MergeMeters + " m are drawn as a few merged meshes per area (far fewer draw calls), with the game's own level of detail.");
            if (!_enabled.Value || !Plugin.GameMatches(Name)) return;
            var field = AccessTools.Field(typeof(Piece), "s_allPieces");
            var health = AccessTools.Method(typeof(WearNTear), "SetHealthVisual");
            var destroy = AccessTools.Method(typeof(Piece), "OnDestroy");
            if (field == null || !field.IsStatic || field.FieldType != typeof(List<Piece>) || health == null || destroy == null)
            {
                Plugin.Log.LogWarning($"{Name}: the game's piece list or piece events did not look as checked, off");
                _enabled = null;
                return;
            }
            _allPieces = AccessTools.StaticFieldRefAccess<List<Piece>>(field);
            harmony.Patch(health, prefix: new HarmonyMethod(AccessTools.Method(typeof(PieceBatching), nameof(HealthBefore))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PieceBatching), nameof(HealthVisual))));
            Benchmark.Start();
            harmony.Patch(destroy, prefix: new HarmonyMethod(AccessTools.Method(typeof(PieceBatching), nameof(PieceGone))));
            Plugin.Log.LogInfo($"{Name}: on, cells of {CellSize} m merge beyond {MergeMeters} m and come apart within {UnmergeMeters} m");
        }

        // the game sets the new, worn and broken looks on every wear update (about 50 pieces a frame), mostly to what they
        // already are: which looks were on before its call
        private static void HealthBefore(WearNTear __instance, out int __state) => __state = Looks(__instance);

        // a piece really changed its look (damage, repair): its merged cell comes apart now and merges later
        private static void HealthVisual(WearNTear __instance, int __state)
        {
            if (Looks(__instance) == __state) return;
            _switches++;
            var piece = __instance.GetComponent<Piece>();
            if (piece != null) Changed(piece);
        }

        private static int Looks(WearNTear wear) =>
            (wear.m_new != null && wear.m_new.activeSelf ? 1 : 0) | (wear.m_worn != null && wear.m_worn.activeSelf ? 2 : 0) | (wear.m_broken != null && wear.m_broken.activeSelf ? 4 : 0);

        private static void PieceGone(Piece __instance) => Changed(__instance);

        private static void Changed(Piece piece)
        {
            if (!PieceCell.TryGetValue(piece, out var cell)) return;
            if (cell.Merged) Unmerge(cell);
            cell.NotBefore = Time.time + CooldownSeconds;
        }

        // once a frame from the plugin
        internal static void Frame()
        {
            if (_enabled == null || !_enabled.Value || _allPieces == null) return;
            var cam = Utils.GetMainCamera();
            if (cam == null || Player.m_localPlayer == null)
            {
                if (Cells.Count > 0) Reset();
                return;
            }
            if (_root == null)
            {
                if (Cells.Count > 0) Reset();
                _root = new GameObject("GapCloser merged pieces");
            }
            if (!_partOn) return;
            var camPos = cam.transform.position;
            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + ScanSeconds;
                Scan();
            }
            if (Time.time >= _nextStat)
            {
                _nextStat = Time.time + StatSeconds;
                int merged = 0, inCells = 0;
                foreach (var cell in Cells.Values)
                {
                    inCells += cell.Pieces.Count;
                    if (cell.Merged) merged++;
                }
                if (Cells.Count > 0)
                    Plugin.Log.LogInfo($"{Name} {StatSeconds:0} s: {Cells.Count} cells ({merged} merged now) with {inCells} pieces, {Skipped.Count} pieces left apart; {_merges} merges ({_mergedPieces} pieces, {(_merges > 0 ? _mergeMs / _merges : 0):0.0} ms each), {_unmerges} came apart, {_switches} looks switched");
                _merges = _unmerges = _mergedPieces = _switches = 0;
                _mergeMs = 0;
            }
            Cell next = null;
            float nextDist = float.MaxValue;
            foreach (var cell in Cells.Values)
            {
                float dist = Mathf.Sqrt(cell.Bounds.SqrDistance(camPos));
                if (cell.Merged)
                {
                    if (dist < UnmergeMeters) Unmerge(cell);
                    else if (Vector3.Distance(camPos, cell.BuiltAt) > RefreshMeters && dist < nextDist && Time.time >= cell.NotBefore)
                    {
                        next = cell;
                        nextDist = dist;
                    }
                    continue;
                }
                if (dist > MergeMeters && cell.Pieces.Count >= MinPieces && Time.time >= cell.NotBefore && dist < nextDist)
                {
                    next = cell;
                    nextDist = dist;
                }
            }
            // one merge a frame, the nearest first
            if (next == null) return;
            if (next.Merged) Unmerge(next);
            Merge(next, camPos, cam);
        }

        // new pieces into their cells (within the frame's budget), pieces gone out of them
        private static void Scan()
        {
            var all = _allPieces();
            if (all == null) return;
            Watch.Restart();
            int reads = 0;
            foreach (var piece in all)
            {
                if (piece == null || PieceCell.ContainsKey(piece) || Skipped.Contains(piece)) continue;
                if (reads >= MinReads && Watch.Elapsed.TotalMilliseconds >= ReadBudgetMs)
                {
                    _nextScan = 0f;
                    break;
                }
                reads++;
                if (!Eligible(piece))
                {
                    Skipped.Add(piece);
                    continue;
                }
                var p = piece.transform.position;
                var key = new Vector3Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));
                if (!Cells.TryGetValue(key, out var cell))
                {
                    cell = new Cell { Key = key, Bounds = new Bounds(((Vector3)key + Vector3.one * 0.5f) * CellSize, Vector3.one * CellSize) };
                    Cells[key] = cell;
                }
                if (cell.Merged) Unmerge(cell);
                cell.NotBefore = Mathf.Max(cell.NotBefore, Time.time + CooldownSeconds);
                cell.Pieces.Add(piece);
                PieceCell[piece] = cell;
            }
            Gone.Clear();
            foreach (var cell in Cells.Values)
            {
                if (cell.Pieces.RemoveAll(IsGone) > 0 && cell.Merged) Unmerge(cell);
                if (cell.Pieces.Count == 0 && !cell.Merged) Gone.Add(cell);
            }
            foreach (var cell in Gone) Cells.Remove(cell.Key);
            if (PieceCell.Count > 4 * all.Count + 1000)
            {
                var stale = new List<Piece>();
                foreach (var pair in PieceCell) if (pair.Key == null) stale.Add(pair.Key);
                foreach (var piece in stale) PieceCell.Remove(piece);
            }
            if (Skipped.Count > 50000) Skipped.Clear();
        }

        // a still building piece with nothing you can use, no lights, effects, animation or moving parts
        private static bool Eligible(Piece piece)
        {
            var view = piece.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || piece.GetComponent<WearNTear>() == null) return false;
            if (piece.GetComponentInParent<Rigidbody>() != null || piece.GetComponentInChildren<Interactable>(true) != null || piece.GetComponentInChildren<Hoverable>(true) != null) return false;
            if (piece.GetComponentInChildren<Light>(true) != null || piece.GetComponentInChildren<ParticleSystem>(true) != null || piece.GetComponentInChildren<Animator>(true) != null) return false;
            if (piece.GetComponentInChildren<SkinnedMeshRenderer>(true) != null || piece.GetComponent<Door>() != null) return false;
            return true;
        }

        private static void Merge(Cell cell, Vector3 camPos, Camera cam)
        {
            Watch.Restart();
            var groups = new Dictionary<GroupKey, List<Group>>();
            var picked = new List<Renderer>();
            var off = new List<Renderer>();
            var origin = Matrix4x4.Translate(-cell.Bounds.center);
            float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            int pieces = 0;
            foreach (var piece in cell.Pieces)
            {
                if (piece == null) continue;
                // occlusion and the sun test let go of the piece first, so its renderers show their own settings; a piece
                // with nothing to merge goes straight back
                OcclusionCulling.SetBatched(piece, true);
                picked.Clear();
                if (!PickRenderers(piece, camPos, tanHalf, picked, off))
                {
                    OcclusionCulling.SetBatched(piece, false);
                    continue;
                }
                cell.Released.Add(piece);
                pieces++;
                foreach (var renderer in picked)
                {
                    var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    var materials = renderer.sharedMaterials;
                    var key = new GroupKey { Shadows = renderer.shadowCastingMode, Receive = renderer.receiveShadows, Layer = renderer.gameObject.layer, LightProbes = renderer.lightProbeUsage, ReflectionProbes = renderer.reflectionProbeUsage };
                    var matrix = origin * renderer.localToWorldMatrix;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        key.Material = materials[s];
                        if (!groups.TryGetValue(key, out var list))
                        {
                            list = new List<Group>();
                            groups[key] = list;
                        }
                        int verts = mesh.vertexCount;
                        var group = list.Count > 0 ? list[list.Count - 1] : null;
                        if (group == null || group.Vertices + verts > MaxVertices)
                        {
                            group = new Group { Key = key };
                            list.Add(group);
                        }
                        group.Parts.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = matrix });
                        group.Vertices += verts;
                    }
                }
            }
            int batches = 0, vertices = 0;
            foreach (var list in groups.Values)
                foreach (var group in list)
                {
                    var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = "GapCloser merged pieces" };
                    mesh.CombineMeshes(group.Parts.ToArray(), true, true, false);
                    mesh.RecalculateBounds();
                    mesh.UploadMeshData(true);
                    var go = new GameObject("GapCloser merged " + cell.Key + " " + group.Key.Material.name);
                    go.layer = group.Key.Layer;
                    go.transform.SetParent(_root.transform, false);
                    go.transform.position = cell.Bounds.center;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = group.Key.Material;
                    r.shadowCastingMode = group.Key.Shadows;
                    r.receiveShadows = group.Key.Receive;
                    r.lightProbeUsage = group.Key.LightProbes;
                    r.reflectionProbeUsage = group.Key.ReflectionProbes;
                    cell.Batches.Add(go);
                    // occlusion and the sun test hide the merged mesh like a piece
                    OcclusionCulling.AddBatch(r);
                    batches++;
                    vertices += group.Vertices;
                }
            // the pieces' own renderers go off only once the merged meshes stand
            foreach (var renderer in off)
                if (renderer != null && renderer.enabled)
                {
                    renderer.enabled = false;
                    cell.Off.Add(renderer);
                }
            cell.Merged = true;
            cell.BuiltAt = camPos;
            _merges++;
            _mergedPieces += pieces;
            _mergeMs += Watch.Elapsed.TotalMilliseconds;
            if (_loggedMerge) return;
            _loggedMerge = true;
            Plugin.Log.LogInfo($"{Name}: first cell merged at {cell.Bounds.center}: {pieces} pieces, {cell.Off.Count} renderers into {batches} meshes of {vertices} vertices in {Watch.Elapsed.TotalMilliseconds:0.0} ms, first time this session");
        }

        // the renderers of a piece to merge at this distance, and every renderer that goes off for them (all levels of a
        // merged level of detail group). false when nothing of the piece merges
        private static bool PickRenderers(Piece piece, Vector3 camPos, float tanHalf, List<Renderer> picked, List<Renderer> off)
        {
            var wear = piece.GetComponent<WearNTear>();
            var apart = new List<Transform>();
            if (wear.m_wet != null) apart.Add(wear.m_wet.transform);
            if (wear.m_snow != null) apart.Add(wear.m_snow.transform);
            if (wear.m_snowWorn != null) apart.Add(wear.m_snowWorn.transform);
            if (wear.m_snowBroken != null) apart.Add(wear.m_snowBroken.transform);
            var ash = AshRoof(wear);
            if (ash != null) apart.Add(ash.transform);
            bool Apart(Transform t)
            {
                foreach (var a in apart) if (t == a || t.IsChildOf(a)) return true;
                return false;
            }
            var inGroup = new HashSet<Renderer>(new SameRenderer());
            foreach (var lod in piece.GetComponentsInChildren<LODGroup>(false))
            {
                var lods = lod.GetLODs();
                foreach (var level in lods) foreach (var r in level.renderers) if (r != null) inGroup.Add(r);
                if (!lod.enabled || Apart(lod.transform)) continue;
                // unity's choice: the first level whose height on screen (times the lod bias) is above its threshold
                var t = lod.transform;
                var s = t.lossyScale;
                float size = lod.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
                float dist = Vector3.Distance(camPos, t.TransformPoint(lod.localReferencePoint));
                float height = size / (2f * tanHalf * Mathf.Max(dist, 0.01f)) * QualitySettings.lodBias;
                int chosen = -1;
                for (int i = 0; i < lods.Length; i++)
                    if (height >= lods[i].screenRelativeTransitionHeight)
                    {
                        chosen = i;
                        break;
                    }
                // the game would not draw it here: it stays as it is
                if (chosen < 0) continue;
                bool all = true;
                foreach (var r in lods[chosen].renderers)
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy && !Mergeable(r)) all = false;
                if (!all) continue;
                foreach (var r in lods[chosen].renderers)
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy) picked.Add(r);
                foreach (var level in lods) foreach (var r in level.renderers) if (r != null) off.Add(r);
            }
            foreach (var r in piece.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (inGroup.Contains(r) || !r.enabled || Apart(r.transform) || !Mergeable(r)) continue;
                picked.Add(r);
                off.Add(r);
            }
            return picked.Count > 0;
        }

        // an opaque mesh renderer whose mesh can be read, one material per submesh, no property block of its own
        private static bool Mergeable(Renderer renderer)
        {
            if (!(renderer is MeshRenderer) || renderer.HasPropertyBlock()) return false;
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return false;
            var materials = renderer.sharedMaterials;
            if (materials.Length != mesh.subMeshCount) return false;
            foreach (var m in materials)
                if (m == null || m.renderQueue >= TransparentQueue) return false;
            return true;
        }

        // the cell's own renderers back on (only those this turned off and still off), the merged meshes gone
        private static void Unmerge(Cell cell)
        {
            foreach (var go in cell.Batches)
            {
                if (go == null) continue;
                OcclusionCulling.RemoveBatch(go.GetComponent<MeshRenderer>());
                var filter = go.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) UnityEngine.Object.Destroy(filter.sharedMesh);
                UnityEngine.Object.Destroy(go);
            }
            foreach (var renderer in cell.Off)
                if (renderer != null && !renderer.enabled) renderer.enabled = true;
            foreach (var piece in cell.Released)
                if (piece != null) OcclusionCulling.SetBatched(piece, false);
            cell.Batches.Clear();
            cell.Off.Clear();
            cell.Released.Clear();
            cell.Merged = false;
            _unmerges++;
            if (_loggedUnmerge) return;
            _loggedUnmerge = true;
            Plugin.Log.LogInfo($"{Name}: a cell came apart at {cell.Bounds.center} (camera near, a change or a piece gone), first time this session");
        }

        // for the run of all benchmarks: true when cells are merged here now
        internal static bool PartAvailable
        {
            get
            {
                if (_enabled == null || !_enabled.Value || _root == null) return false;
                foreach (var cell in Cells.Values) if (cell.Merged) return true;
                return false;
            }
        }

        internal static bool PartOn => _partOn;

        internal static void SetPart(bool on)
        {
            if (_partOn == on) return;
            _partOn = on;
            MergedView(on);
        }

        // merged: the merged meshes on and the pieces' own renderers off, both tests skip the pieces; apart: the reverse,
        // the pieces back to the camera and sun tests
        private static void MergedView(bool merged)
        {
            foreach (var cell in Cells.Values)
            {
                if (!cell.Merged) continue;
                if (merged)
                {
                    foreach (var piece in cell.Released) if (piece != null) OcclusionCulling.SetBatched(piece, true);
                    foreach (var r in cell.Off) if (r != null && r.enabled) r.enabled = false;
                    foreach (var go in cell.Batches) if (go != null) go.SetActive(true);
                }
                else
                {
                    foreach (var go in cell.Batches) if (go != null) go.SetActive(false);
                    foreach (var r in cell.Off) if (r != null && !r.enabled) r.enabled = true;
                    foreach (var piece in cell.Released) if (piece != null) OcclusionCulling.SetBatched(piece, false);
                }
            }
        }

        // a new world or the menu: everything back, all forgotten
        private static void Reset()
        {
            foreach (var cell in Cells.Values) if (cell.Merged) Unmerge(cell);
            Cells.Clear();
            PieceCell.Clear();
            Skipped.Clear();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
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
