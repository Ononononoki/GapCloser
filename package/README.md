# GapCloser

**Fixes and speed-ups for Valheim and the mods I play with: a faster hammer menu and inventory near many chests, occlusion culling and merged buildings for big bases, fast portals, a rock support view for ore deposits, smoke off for more FPS, other mods' errors guarded.**

> Each fix turns on only for the game and mod versions it was checked against (game 1.0.20). Otherwise it stays off and the log says why, unless you turn on `ForceOnUntestedVersions`. Made for my own modpack, more fixes may come.
>
> **Smoke is off by default** for more FPS. For the game's smoke, set `Smoke = Normal` in the `2 - Options` section of the config.

## All fixes

### The game

- **Fast portals.** A 0.25 s fade, and you arrive as soon as the destination is loaded (the game waits at least 8 s). 0.5 s between trips instead of 2.
- **Occlusion culling.** Building pieces the walls fully hide are not drawn, their shadows stay. On by default on DirectX 11.
- **Hidden lights.** Fires, torches and lamps of buildings whose light the walls fully hide go dark until they can be seen.
- **Merged distant buildings.** Pieces farther than 48 m are drawn as a few merged meshes per area, far fewer draw calls.
- **Smoother loading near big bases.** Objects near you are made within 4 ms a frame, nearest first, instead of up to 100 in one frame.
- **Unchanged chests are not rebuilt** when their open flag changes or the ship under them moves.
- **Building effects pause off screen.** Fires, ovens and portals no camera sees stop simulating and catch up when seen.
- **Light flicker pauses** for lights whose light reaches nothing in view.
- **Rock support view.** Each part of a mine rock you look at is tinted by what breaking it does. Blue holds others, green drops, red is held from outside, and yellow is held by other parts. Shift+O toggles the tint, and the tooltip shows your configured key. Range, linger time, and refresh interval are configurable.
- **Smoke off by default.** No smoke from fires, kilns and smelters: much less physics near many fires, and no Smoked debuff. Fires, kilns and smelters work as before. `Smoke = Normal` brings the game's smoke back.
- **Recipe learning check** once a frame instead of on every inventory change.
- **No warning spam** from stakes and other damage areas.
- **Sun shadow culling** (optional, off by default): pieces hidden from the sun stop casting sun shadows.

### Other mods (each only for the version named)

- **AzuCraftyBoxes 1.8.19:** faster hammer menu near many chests (each material is counted once, not again for every piece), faster inventory and crafting (chest counts kept until a chest changes, chest names kept), the Forge of Potential no longer teaches every recipe, `gapcloser_fixrecipes` removes the recipes it taught, its fermenter error guarded.
- **ValheimPlus 0.10.3.0:** chest counts for crafting from chests without garbage, its breeding and tame hover errors guarded.
- **ExtraSlots 1.2.21:** slot lookups without garbage, its Bows Before Hoes count skipped while that mod is not installed.
- **ValheimCommunityPatch 0.36.1 and 0.37.0:** the smoother loading also works inside its spawn queue.

## Measured at my castle (about 6,000 pieces)

- All parts together (final test, no diagnostics): looking at the castle from outside 70 to 83 fps (+19 %, 2.3 ms a frame), inside 84 to 87 fps (+3 %, 0.3 ms). Smoke off adds about 5 % (0.7 ms).
- Hammer menu with AzuCraftyBoxes: the slowest frame from 230 to 101 ms, the chest counting in it from 182 to about 5 ms.
- Garbage with the inventory open: from 12.2 to 3.9 MB a second.
- Portals: 0.25 s between floors, 2 to 6 s to bases that were not loaded.
- Rock support: 95 of 95 hits dropped exactly the predicted parts.
- Self-checks against the game and AzuCraftyBoxes found no difference in 580 skipped chest rebuilds and 1,566 hammer checks.

## Commands

- `gapcloser_fixrecipes` lists the recipes a character could not have learned, `gapcloser_fixrecipes apply` backs up the character and removes them. Runs by itself on entering a world when AzuCraftyBoxes is installed. In chat, type it with a slash.
- `gapcloser_rocksupport` switches the rock support view.

## Config

`BepInEx/config/onononoki.GapCloser.cfg`. Every fix can be switched off on its own. Sections: `1 - General`, `2 - Options` (Smoke, Off by default), `3 - Fixes`, `4 - Rock Support` (colors, key, range, linger time, refresh interval), `9 - Debug` (logs, self-checks, and the benchmark keys: Home switches occlusion culling to compare, Shift+End measures every part and writes `BepInEx/GapCloser_benchmarks.txt`). Settings from older versions move to the new sections by themselves.

### Rock support settings

These settings are in `4 - Rock Support`.

| Setting | Default | Effect |
|---|---|---|
| `MaxDistance` | `25` | Maximum distance in meters from your character to the aimed rock surface. |
| `ShowSeconds` | `5` | How long the tint stays after looking away. Set `0` for immediate hiding, or use a longer value such as `10` or `30`. |
| `RefreshSeconds` | `1` | Seconds between normal support calculations. Set `0.5` for faster updates. |
| `ToggleKey` | `O + LeftShift` | Shows or hides the tint. The tooltip displays your binding and the current action. |

The range still applies while the tint lingers. Targeting reuses the game's hover ray, which reaches 50 meters from the camera. Support colors refresh at the configured interval, and normal support calculations pause while the tint is hidden. The optional hit diagnostics can calculate outside that interval when `RockSupportLogChecks` is enabled in `9 - Debug`.

## Compatibility

- Client side. Nothing to install on the server, other players do not need it.
- AzuCraftyBoxes, ValheimPlus, ExtraSlots and ValheimCommunityPatch are optional. Made to run next to [ValheimCommunityPatch](https://thunderstore.io/c/valheim/p/MidnightMods/ValheimCommunityPatch/).
- Occlusion culling, hidden lights and sun shadow culling need DirectX 11 for now.
- Occlusion culling and hidden lights leave out pieces that are not buildings, such as PlantEverything plants, saplings and debris, so they never flicker.
- After a game update the game fixes stay off until a new GapCloser checks them, or until you turn on `ForceOnUntestedVersions`.

The following combination was tested in game with GapCloser 1.3.0 on Valheim 1.0.20. These are the tested versions, not a claim about every future release.

| Mod | Tested version | How it runs alongside GapCloser |
|---|---|---|
| ValheimCommunityPatch | `0.37.0` | GapCloser's nearby object budget also runs inside its spawn queue. |
| ValheimOptimized | `1.1.1` | Works alongside GapCloser. Its own loop handles distant spawning, so GapCloser's distant object budget does not run there. The nearby budget still runs through ValheimCommunityPatch's queue when present. |
| Balrond Core Optimizer | `0.2.5` | Works alongside GapCloser with its default Conservative Mode. It covers different parts of the game and delegates overlapping work to ValheimCommunityPatch. |

ValheimOptimized and Balrond Core Optimizer are not made obsolete by GapCloser. With ValheimOptimized loaded, ValheimCommunityPatch steps aside for four overlapping fixes, including hotbar keys and terrain work. Its hotbar stand-down warning is expected in that tested combination. The logs showed no exceptions or errors. [Compatibility test details](https://github.com/Ononononoki/GapCloser/issues/1).

### Dedicated teleport timing mods

GapCloser automatically skips only its `FastTeleport` patches when OdinPlus QuickTeleport or Muindor QuickTeleport is loaded. The other mod takes priority for teleport timing. Other GapCloser features remain active, and ValheimCommunityPatch alone does not disable FastTeleport.

Detection runs once after plugin creation and matches plugin IDs, not every mod that touches a teleport method. The `FastTeleportOtherModIDs` setting in `3 - Fixes` defaults to `elg.QuickTeleport,muindor.QuickTeleport`. Add a comma-separated plugin ID for another dedicated teleport timing mod. Clear the list to disable automatic detection, or set `FastTeleport = false` to disable GapCloser's teleport changes manually. Restart after changing the list or installed mods.

The saved `FastTeleport` value is left unchanged. Removing the other timing mod lets GapCloser's version run again on the next start if that setting is true. QuickTeleport detection was checked offline. Teleporting with QuickTeleport installed has not been tested in game.

## Credits

- The rock support view's idea is from [Ore Support](https://thunderstore.io/c/valheim/p/JereKuusela/Ore_Support/) by JereKuusela. The code follows the game's own support rule.
- Hidden lights and building merging were inspired by [ValheimBuildOptimization](https://thunderstore.io/c/valheim/p/RockBreaker/ValheimBuildOptimization/) by RockBreaker (Dracoalias). No code was copied.

## Feedback

Found a bug, or a gap in another mod that GapCloser could close? Open an issue on [GitHub](https://github.com/Ononononoki/GapCloser/issues).
