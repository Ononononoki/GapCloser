# GapCloser

**Closes the gaps other mods leave. Portals without the wait, a recipe bug of AzuCraftyBoxes fixed and cleaned up, other mods' errors guarded.**

> Made for my own play and my own modpack, not a fix-all. Each fix turns on only for the game and mod versions it was checked against (game 1.0.20). Otherwise it stays off and the log says why, unless you turn on `ForceOnUntestedVersions`.
>
> More fixes may come over time, for the gaps I find in the mods I play with.

## What it does

- **Fast portals.** A 0.25 s fade, and you arrive as soon as the destination is loaded. The game waits at least 8 s on every portal trip.
- **Forge of Potential fix** (AzuCraftyBoxes 1.8.19). An upgrade there no longer teaches every recipe you have the station for.
- **Faster inventory near many chests** (AzuCraftyBoxes 1.8.19). Each chest's count of a material is kept until that chest changes, instead of being counted again for every recipe every frame.
- **Recipe cleanup.** Removes the recipes a character could not have learned, after a backup. Runs on world entry when AzuCraftyBoxes is installed.
- **Building effects pause off screen.** Fires, ovens and portals no camera sees stop simulating and catch up when seen again. Lights are not touched.
- **Other mods' errors guarded.** ValheimPlus 0.10.3.0 breeding and tame hover, AzuCraftyBoxes 1.8.19 fermenter.
- **Game fixes.** No warning spam from stakes, and the recipe discovery check once a frame instead of on every inventory change.

## Measured in my game

- **Portals:** between the floors of my castle 0.22 to 0.26 s. To far bases that were not loaded (0.4 to 5.9 km) 2.2 to 5.9 s on an SSD.
- **Inventory open at my castle** (AzuCraftyBoxes, many chests in range): stutters over 40 ms from 71 to 35 a minute, the opening frame from about 60 to 47 ms, garbage from 20 to 12 MB a second.
- **Building effects:** the castle's particle work from 1.00 to 0.62 ms a frame.
- **Recipe cleanup:** 130 wrongly learned recipes removed from a test character, with a backup.
- **Errors:** 7 to 10 errors from other mods per play session before, none after.
- **A big castle (about 6,000 building pieces), together with [ValheimCommunityPatch](https://thunderstore.io/c/valheim/p/MidnightsFX/ValheimCommunityPatch/)** (its performance fixes on, its gameplay changes off): average frame from 20.3 to about 17 ms, stutters over 40 ms from 9 to under 3 a minute, building upkeep from 2.9 to 0.9 ms a frame. Most of that is ValheimCommunityPatch's building upkeep fix. GapCloser adds what it does not cover.

## Console command

`gapcloser_fixrecipes` lists the recipes the character could not have learned (all of them in `LogOutput.log`). `gapcloser_fixrecipes apply` backs up the character and removes them. In chat, type `/gapcloser_fixrecipes` with the slash.

A Steam Cloud character gets the game's own backup, a local one a copy in `BepInEx/config/GapCloser_backups`. A removed recipe comes back the normal way once you know its materials.

## Config

The file is `BepInEx/config/onononoki.GapCloser.cfg`. Every fix can be switched off on its own.

| Setting | Default | What it does |
|---|---|---|
| `General.ForceOnUntestedVersions` | false | Turns the fixes on also for game and mod versions they were not checked against (at your own risk) |
| `Fixes.AzuCraftyBoxesForgeRecipes` | true | The Forge of Potential recipe fix (AzuCraftyBoxes 1.8.19) |
| `Fixes.AzuCraftyBoxesFermenterError` | true | Guards the fermenter error (AzuCraftyBoxes 1.8.19) |
| `Fixes.AzuCraftyBoxesCraftingList` | true | Keeps chest counts, faster inventory near many chests (AzuCraftyBoxes 1.8.19) |
| `Fixes.ValheimPlusProcreationError` | true | Guards the breeding error (ValheimPlus 0.10.3.0) |
| `Fixes.ValheimPlusTameHoverError` | true | Guards the tame hover error (ValheimPlus 0.10.3.0) |
| `Fixes.TriggerWarningSpam` | true | Stops the trigger damage warning spam |
| `Fixes.RecipeChecksOncePerFrame` | true | The recipe discovery check once a frame |
| `Fixes.RecipeCleanupCommand` | true | The `gapcloser_fixrecipes` command and the cleanup |
| `Fixes.RecipeCleanupOnSpawn` | WithAzuCraftyBoxes | When the cleanup runs by itself on entering a world: WithAzuCraftyBoxes, Always or Off |
| `Fixes.FastTeleport` | true | Fast portals and teleports |
| `Fixes.FastTeleportFadeSeconds` | 0.25 | The fade to black and back (the game uses 1) |
| `Fixes.PieceParticleCulling` | true | Building effects pause off screen |
| `Debug.LogTeleports` | false | One log line per trip with its timings, and one for the 5 s after arrival |

## Compatibility

- Client side. Nothing to install on the server, and other players do not need it.
- AzuCraftyBoxes and ValheimPlus are not needed. Their fixes switch on only when the checked version is installed.
- Made to run next to ValheimCommunityPatch.
- After a game update the game fixes stay off until a new version of GapCloser checks them, or until you turn on `ForceOnUntestedVersions`.

## Feedback and suggestions

Found a bug, or a gap in another mod that GapCloser could close? Open an issue on [GitHub](https://github.com/Ononononoki/GapCloser/issues). Suggestions are welcome.
