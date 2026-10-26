using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // ValheimPlus 0.10.3.0 and 10.4.0.0 resize a station's or ward's spawn protection only on the first effect area under it, and only
    // through a SphereCollider. the workbench, the forge and the artisan table carry theirs as a CapsuleCollider (prefab radius 20), so
    // they kept 20 m while the stonecutter and the ward got the cfg's range. after ValheimPlus' resize every effect area of the asked
    // type under the object gets the same radius, capsules too (their height scaled along), with the radius ValheimPlus worked out from
    // its own cfg
    internal static class ValheimPlusSpawnArea
    {
        private const string Name = "ValheimPlusSpawnArea";
        private const string TargetType = "ValheimPlus.Helper";
        private const string TargetMethod = "ResizeChildEffectArea";
        private static readonly string[] CheckedVersions = { "0.10.3.0", "10.4.0.0" };
        // the first stations resized are named in the log, then a count now and then
        private const int NamedObjects = 5;
        private static int _resized;
        private static bool _warned;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Sections.Fixes, Name, true,
                "ValheimPlus 0.10.3.0 and 10.4.0.0: its spawn protection range for workbenches and wards (workbenchEnemySpawnRange, wardEnemySpawnRange) also reaches stations whose area is a capsule (workbench, forge, artisan table), not only the stonecutter. Off: those keep the game's 20 m.");
            if (!enabled.Value || !Plugin.TargetMatches(Name, ValheimPlusProcreation.TargetGuid, CheckedVersions)) return;
            var type = AccessTools.TypeByName(TargetType);
            var target = type == null ? null : AccessTools.Method(type, TargetMethod, new[] { typeof(MonoBehaviour), typeof(EffectArea.Type), typeof(float) });
            if (target == null)
            {
                Plugin.Log.LogWarning($"{Name}: {TargetType}.{TargetMethod} not found, the fix stays off");
                return;
            }
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(ValheimPlusSpawnArea), nameof(Postfix))));
                Plugin.Log.LogInfo($"{Name}: on for ValheimPlus {string.Join(", ", CheckedVersions)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Name}: could not patch {TargetType}.{TargetMethod}, the fix stays off: {e.Message}");
            }
        }

        // __0 is the station or ward, __1 the asked area type, __2 the radius ValheimPlus set from its cfg
        private static void Postfix(MonoBehaviour __0, EffectArea.Type __1, float __2)
        {
            try
            {
                if (__0 == null || __2 <= 0f) return;
                int changed = 0;
                float before = 0f;
                // active areas only, as ValheimPlus' own lookup
                foreach (var area in __0.GetComponentsInChildren<EffectArea>())
                {
                    if ((area.m_type & __1) == 0) continue;
                    var sphere = area.GetComponent<SphereCollider>();
                    if (sphere != null && !Mathf.Approximately(sphere.radius, __2)) { before = sphere.radius; sphere.radius = __2; changed++; }
                    var capsule = area.GetComponent<CapsuleCollider>();
                    if (capsule != null && capsule.radius > 0f && !Mathf.Approximately(capsule.radius, __2))
                    {
                        before = capsule.radius;
                        capsule.height *= __2 / capsule.radius;
                        capsule.radius = __2;
                        changed++;
                    }
                }
                if (changed == 0) return;
                _resized++;
                if (_resized <= NamedObjects || _resized % 50 == 0)
                    Plugin.Log.LogInfo($"{Name}: {__0.name} spawn protection {before:0.#} -> {__2:0.#} m ({changed} area{(changed == 1 ? "" : "s")}), {_resized} stations so far");
            }
            catch (Exception e)
            {
                if (_warned) return;
                _warned = true;
                Plugin.Log.LogWarning($"{Name}: a spawn protection area could not be resized, left as ValheimPlus set it: {e.Message}");
            }
        }
    }
}
