# GapCloser

**Closes the gaps other mods leave. A rock support view for ore deposits, occlusion culling for big bases, portals without the wait, less garbage near chests, a recipe bug of AzuCraftyBoxes fixed and cleaned up, other mods' errors guarded.**

> Made for my own play and my own modpack, not a fix-all. Each fix turns on only for the game and mod versions it was checked against (game 1.0.20). Otherwise it stays off and the log says why, unless you turn on `ForceOnUntestedVersions`.
>
> More fixes may come over time, for the gaps I find in the mods I play with.

## What it does

- **Rock support view (new in 1.2.0).** Every part of the mine rock you look at or hit (copper, silver and other big deposits) is tinted by what breaking it does: **blue** holds other parts up, **green** drops when a blue part breaks (or is loose already), **red** is held by the ground, **yellow** is held by other parts. The hover text gives the counts. It works out the game's own support rule exactly, from the same boxes and checks the game uses. Shift+O hides the tint. The idea comes from [Ore Support](https://thunderstore.io/c/valheim/p/JereKuusela/Ore_Support/) by JereKuusela, the code is written from the game's own logic.
- **Occlusion culling (new in 1.2.0, still being tested).** Building pieces that walls hide completely are not drawn, their shadows stay. The test runs on the graphics card with the camera's own depth image. On by default on DirectX 11, off on other graphics APIs (the log says so). Home switches it while playing, Shift+Home measures it for 20 s and writes the result to the log.
- **Less garbage near chests (new in 1.2.0).** ValheimPlus 0.10.3.0 crafting from chests counts chest items without new lists every frame, ExtraSlots 1.2.21 finds its slots without new memory and skips its Bows Before Hoes count when that mod is not installed, AzuCraftyBoxes 1.8.19 keeps its chest names. Fewer garbage collection stutters with the inventory open.
- **Fast portals.** A 0.25 s fade, and you arrive as soon as the destination is loaded. The game waits at least 8 s on every portal trip.
- **Forge of Potential fix** (AzuCraftyBoxes 1.8.19). An upgrade there no longer teaches every recipe you have the station for.
- **Faster inventory near many chests** (AzuCraftyBoxes 1.8.19). Each chest's count of a material is kept until that chest changes, instead of being counted again for every recipe every frame.
- **Recipe cleanup.** Removes the recipes a character could not have learned, after a backup. Runs on world entry when AzuCraftyBoxes is installed.
- **Building effects pause off screen.** Fires, ovens and portals no camera sees stop simulating and catch up when seen again. Lights are not touched.
- **Other mods' errors guarded.** ValheimPlus 0.10.3.0 breeding and tame hover, AzuCraftyBoxes 1.8.19 fermenter.
- **Game fixes.** No warning spam from stakes, and the recipe discovery check once a frame instead of on every inventory change.

## Measured in my game

- **Rock support:** 95 hits on copper and silver deposits checked against what the game really dropped, all 95 the same (one hit dropped 72 parts, as shown).
- **Occlusion culling at my castle** (a heavy build, about 6,000 pieces): 0.7 to 1.4 ms faster a frame in 5 of 5 benchmarks (about 5 to 8 %), 1,800 to 5,300 pieces hidden, no visual problems seen. A small gain, more on bigger builds.
- **Garbage with the inventory open at my castle** (ValheimPlus, ExtraSlots, AzuCraftyBoxes): from 12.2 to 3.9 MB a second, garbage collections from 4.8 to 1.6 a minute.
- **Portals:** between the floors of my castle 0.22 to 0.26 s. To far bases that were not loaded (0.4 to 5.9 km) 2.2 to 5.9 s on an SSD.
- **Inventory open at my castle** (AzuCraftyBoxes, many chests in range): stutters over 40 ms from 71 to 35 a minute, the opening frame from about 60 to 47 ms, garbage from 20 to 12 MB a second.
- **Building effects:** the castle's particle work from 1.00 to 0.62 ms a frame.
- **Recipe cleanup:** 130 wrongly learned recipes removed from a test character, with a backup.
- **Errors:** 7 to 10 errors from other mods per play session before, none after.
- **A big castle (about 6,000 building pieces), together with [ValheimCommunityPatch](https://thunderstore.io/c/valheim/p/MidnightsFX/ValheimCommunityPatch/)** (its performance fixes on, its gameplay changes off): average frame from 20.3 to about 17 ms, stutters over 40 ms from 9 to under 3 a minute, building upkeep from 2.9 to 0.9 ms a frame. Most of that is ValheimCommunityPatch's building upkeep fix. GapCloser adds what it does not cover.

## Console command

`gapcloser_fixrecipes` lists the recipes the character could not have learned (all of them in `LogOutput.log`). `gapcloser_fixrecipes apply` backs up the character and removes them. In chat, type `/gapcloser_fixrecipes` with the slash.

A Steam Cloud character gets the game's own backup, a local one a copy in `BepInEx/config/GapCloser_backups`. A removed recipe comes back the normal way once you know its materials.

`gapcloser_rocksupport` switches the rock support view on and off.

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
| `Fixes.ValheimPlusChestCounts` | true | Chest counts without garbage for crafting from chests (ValheimPlus 0.10.3.0) |
| `Fixes.ExtraSlotsFindSlot` | true | Slot lookups without garbage (ExtraSlots 1.2.21) |
| `Fixes.ExtraSlotsCountItems` | true | Skips the Bows Before Hoes quiver count when that mod is not installed (ExtraSlots 1.2.21) |
| `Fixes.AzuCraftyBoxesChestNames` | true | Keeps chest names instead of building them again (AzuCraftyBoxes 1.8.19) |
| `Fixes.OcclusionCulling` | On | Hides building pieces the walls hide completely: On, LookOnly (tests and logs only) or Off. DirectX 11 only unless `ForceOnUntestedVersions` |
| `Fixes.OcclusionCullingKey` | Home | Switches the hiding while playing, to compare |
| `Fixes.OcclusionCullingBenchmarkKey` | Home + LeftShift | Stand still and press it: 20 s of hiding on and off, the result in the log |
| `Fixes.OcclusionCullingLogLines` | false | For testing: a log line every 5 s and a summary every 30 s while it runs |
| `RockSupport.Enabled` | true | The rock support view |
| `RockSupport.ToggleKey` | O + LeftShift | Hides or shows the tint (the hover lines stay), shown again at every game start |
| `RockSupport.ShowAnchors` | true | Also tints the parts whose loss drops nothing (red and yellow) |
| `RockSupport.BestColor`, `FallsColor`, `AnchorColor`, `HeldColor` | blue, green, red, yellow | The four tints, see-through |
| `RockSupport.SelfCheck` | false | For testing: compares the view with the game's own check every few seconds and logs a difference |
| `RockSupport.LogChecks` | false | For testing: a log line for each hit that breaks a part (predicted and fallen) and a summary every 60 s |
| `Debug.LogTeleports` | false | One log line per trip with its timings, and one for the 5 s after arrival |

## Compatibility

- Client side. Nothing to install on the server, and other players do not need it.
- AzuCraftyBoxes, ValheimPlus and ExtraSlots are not needed. Their fixes switch on only when the checked version is installed.
- Made to run next to ValheimCommunityPatch.
- Ore Support can stay installed next to the rock support view, then both show.
- Occlusion culling needs a graphics card with compute shaders. On DirectX 12, Vulkan, Metal or OpenGL it stays off for now.
- After a game update the game fixes stay off until a new version of GapCloser checks them, or until you turn on `ForceOnUntestedVersions`.

## Feedback and suggestions

Found a bug, or a gap in another mod that GapCloser could close? Open an issue on [GitHub](https://github.com/Ononononoki/GapCloser/issues). Suggestions are welcome.
