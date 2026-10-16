using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GapCloser.Fixes
{
    // the game checks every unknown recipe again on every inventory change, several times in one frame when a craft or an
    // upgrade moves several items (up to 1,567 checks in one frame at the forge of potential). the first check of a frame
    // runs at once as before, the others become one check at the end of the same frame. the same recipes are learned
    internal static class RecipeChecks
    {
        private const string Name = "RecipeChecksOncePerFrame";
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static Action<Player> _check;
        private static Action<InventoryGui, bool> _refreshPanel;
        private static Player _pending;
        private static int _frame = -1;
        private static bool _on, _flushing, _actedOnce;

        public static void Apply(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind("Fixes", Name, true,
                "Runs the game's recipe learning check at most twice a frame (at once, then once more at the end of the frame) instead of on every inventory change. The same recipes are learned. Works only with the checked game versions.");
            if (!enabled.Value || !Plugin.GameMatches(Name)) return;
            var target = AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
            harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(RecipeChecks), nameof(Prefix))));
            // the patched method itself, so the other mods' patches on it still run
            _check = AccessTools.MethodDelegate<Action<Player>>(target);
            _refreshPanel = AccessTools.MethodDelegate<Action<InventoryGui, bool>>(AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel"));
            _on = true;
            Plugin.Log.LogInfo($"{Name}: on");
        }

        private static bool Prefix(Player __instance)
        {
            if (_flushing || __instance == null || __instance != Player.m_localPlayer) return true;
            if (Time.frameCount != _frame)
            {
                _frame = Time.frameCount;
                return true;
            }
            _pending = __instance;
            if (!_actedOnce)
            {
                _actedOnce = true;
                Plugin.Log.LogInfo($"{Name}: merged a repeated recipe check into the end of the frame, first time this session");
            }
            return false;
        }

        // called from the plugin's LateUpdate, after every other script of the frame
        public static void Flush()
        {
            if (!_on || _pending == null) return;
            var player = _pending;
            _pending = null;
            if (player == null || player != Player.m_localPlayer) return;
            int before = KnownRecipes(player).Count;
            _flushing = true;
            try
            {
                _check(player);
            }
            finally
            {
                _flushing = false;
            }
            // a recipe learned in the merged check shows in an open crafting list at once, as it would have before
            if (KnownRecipes(player).Count != before && InventoryGui.instance != null && InventoryGui.IsVisible())
                _refreshPanel(InventoryGui.instance, false);
        }
    }
}
