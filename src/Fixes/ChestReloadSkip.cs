using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // newer, still being tested: every chest checks itself once a second and rebuilds its whole inventory (each item made
    // and destroyed, 2 to 8 ms a chest) whenever anything of its network object changed: its own open flag, and for ship
    // and cart chests every move of the ship or cart. before such a rebuild this writes what the chest holds now with the
    // game's own save and compares it byte for byte with the stored items: equal means the rebuild would make exactly the
    // same items, so it is left out. any difference, an open chest, a tombstone or anything unexpected goes through the
    // game's own load as before. it never writes anything
    internal static class ChestReloadSkip
    {
        private const string Name = "ChestReloadSkip";
        private const float StatSeconds = 60f;

        private static ConfigEntry<bool> _enabled, _selfCheck;
        private static AccessTools.FieldRef<Container, ZNetView> _nview;
        private static AccessTools.FieldRef<Container, Inventory> _inventory;
        private static AccessTools.FieldRef<Container, uint> _lastRevision;
        private static AccessTools.FieldRef<Container, bool> _inUse;
        private static int _itemsKey;
        private static readonly ZPackage Package = new ZPackage();
        private static float _nextStat;
        private static int _skipped, _loaded, _match, _different, _checkFailed;
        private static bool _loggedSkip;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            _enabled = config.Bind(Sections.Fixes, Name, true,
                "A chest whose stored items did not change is not rebuilt when its open flag changes or the ship or cart under it moves.");
            _selfCheck = config.Bind(Sections.Debug, Name + "SelfCheck", false,
                "Test: each skipped chest rebuild is also checked item by item, the log gets MATCH or DIFFERENT.");
            if (!_enabled.Value || !Plugin.GameMatches(Name)) return;
            var load = AccessTools.Method(typeof(Container), "Load");
            var nview = AccessTools.Field(typeof(Container), "m_nview");
            var inventory = AccessTools.Field(typeof(Container), "m_inventory");
            var revision = AccessTools.Field(typeof(Container), "m_lastRevision");
            var inUse = AccessTools.Field(typeof(Container), "m_inUse");
            var items = AccessTools.Field(typeof(ZDOVars), "s_items");
            if (load == null || load.ReturnType != typeof(bool) || load.GetParameters().Length != 0 || nview?.FieldType != typeof(ZNetView) || inventory?.FieldType != typeof(Inventory)
                || revision?.FieldType != typeof(uint) || inUse?.FieldType != typeof(bool) || items == null || items.FieldType != typeof(int))
            {
                Plugin.Log.LogWarning($"{Name}: the game's chest load did not look as checked, off");
                return;
            }
            _nview = AccessTools.FieldRefAccess<Container, ZNetView>(nview);
            _inventory = AccessTools.FieldRefAccess<Container, Inventory>(inventory);
            _lastRevision = AccessTools.FieldRefAccess<Container, uint>(revision);
            _inUse = AccessTools.FieldRefAccess<Container, bool>(inUse);
            _itemsKey = (int)items.GetValue(null);
            harmony.Patch(load, prefix: new HarmonyMethod(AccessTools.Method(typeof(ChestReloadSkip), nameof(Before))) { priority = Priority.Last });
            Plugin.Log.LogInfo($"{Name}: on{(_selfCheck.Value ? ", self-check on (every left out rebuild is checked)" : "")}");
        }

        // false leaves the game's load out: the chest keeps its items, takes the new revision and reports a load, so the
        // game still updates its open look
        private static bool Before(Container __instance, ref bool __result)
        {
            try
            {
                var view = _nview(__instance);
                var zdo = view != null ? view.GetZDO() : null;
                if (zdo == null) return true;
                uint revision = zdo.DataRevision;
                // the game's own quick answers: nothing new, or open right now
                if (revision == _lastRevision(__instance) || _inUse(__instance)) return true;
                var stored = zdo.GetByteArray(_itemsKey);
                var inventory = _inventory(__instance);
                if (stored == null || inventory == null || __instance.GetComponent<TombStone>() != null) return true;
                Package.Clear();
                inventory.Save(Package);
                if (!Same(Package.GetArray(), stored))
                {
                    _loaded++;
                    return true;
                }
                if (_selfCheck.Value) Check(__instance, inventory, stored);
                _lastRevision(__instance) = revision;
                __result = true;
                _skipped++;
                if (!_loggedSkip)
                {
                    _loggedSkip = true;
                    Plugin.Log.LogInfo($"{Name}: left out the rebuild of {__instance.name} at {__instance.transform.position}, its stored items are what it holds, first time this session");
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: {e.GetType().Name} ({e.Message}), the game's load runs");
                return true;
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // the stored items loaded into a spare inventory, compared item by item with the chest's
        private static void Check(Container chest, Inventory inventory, byte[] stored)
        {
            string problem;
            try
            {
                var spare = new Inventory(inventory.GetName(), null, inventory.GetWidth(), inventory.GetHeight());
                spare.Load(new ZPackage(stored));
                problem = Difference(inventory.GetAllItems(), spare.GetAllItems());
            }
            catch (Exception e)
            {
                _checkFailed++;
                Plugin.Log.LogWarning($"{Name}: self-check could not load the stored items of {chest.name} ({e.GetType().Name}: {e.Message})");
                return;
            }
            if (problem == null)
            {
                _match++;
                return;
            }
            _different++;
            Plugin.Log.LogWarning($"{Name}: self-check DIFFERENT for {chest.name} at {chest.transform.position}: {problem}");
        }

        private static string Difference(List<ItemDrop.ItemData> held, List<ItemDrop.ItemData> stored)
        {
            if (held.Count != stored.Count) return $"{held.Count} items held, {stored.Count} stored";
            for (int i = 0; i < held.Count; i++)
            {
                var a = held[i];
                var b = stored[i];
                // prefabs of the object list are never destroyed, a plain null check is enough
                string nameA = ReferenceEquals(a.m_dropPrefab, null) ? a.m_shared?.m_name : a.m_dropPrefab.name;
                string nameB = ReferenceEquals(b.m_dropPrefab, null) ? b.m_shared?.m_name : b.m_dropPrefab.name;
                if (nameA != nameB || a.m_stack != b.m_stack || a.m_quality != b.m_quality || a.m_variant != b.m_variant || a.m_gridPos.x != b.m_gridPos.x || a.m_gridPos.y != b.m_gridPos.y
                    || Math.Abs(a.m_durability - b.m_durability) > 0.001f || a.m_crafterID != b.m_crafterID || a.m_crafterName != b.m_crafterName
                    || a.m_worldLevel != b.m_worldLevel || a.m_pickedUp != b.m_pickedUp || !SameData(a.m_customData, b.m_customData))
                    return $"item {i}: held {nameA} x{a.m_stack} q{a.m_quality} at {a.m_gridPos}, stored {nameB} x{b.m_stack} q{b.m_quality} at {b.m_gridPos}";
            }
            return null;
        }

        private static bool SameData(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            int countA = a?.Count ?? 0, countB = b?.Count ?? 0;
            if (countA != countB) return false;
            if (countA == 0) return true;
            foreach (var pair in a)
                if (!b.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
            return true;
        }

        // once a frame from the plugin: the counts every minute while chests were checked
        internal static void Frame()
        {
            if (_inventory == null || Time.time < _nextStat) return;
            _nextStat = Time.time + StatSeconds;
            if (_skipped + _loaded == 0) return;
            Plugin.Log.LogInfo($"{Name} {StatSeconds:0} s: {_skipped} rebuilds left out, {_loaded} chests loaded new items" +
                               (_selfCheck.Value ? $"; self-check {_match} MATCH, {_different} DIFFERENT, {_checkFailed} not checked" : ""));
            _skipped = _loaded = _match = _different = _checkFailed = 0;
        }
    }
}
