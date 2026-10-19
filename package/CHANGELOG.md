# Changelog

## 1.3.1

- Added the tested ValheimOptimized, Balrond Core Optimizer, and ValheimCommunityPatch versions and overlap details to the compatibility section.
- FastTeleport automatically steps aside for OdinPlus or Muindor QuickTeleport. `FastTeleportOtherModIDs` can list other dedicated teleport timing mods. The saved FastTeleport value is preserved, and ValheimCommunityPatch alone does not disable it.

- Rock support view now uses the game's existing hover ray. No additional camera raycast, and interaction reach stays the same.
- Configurable rock view range and linger time. Defaults are 25 meters and 5 seconds after looking away. Set `ShowSeconds = 0` for immediate hiding.
- The rock tooltip shows the configured toggle key and whether it will show or hide the tint. The default remains Shift+O.
- Normal support calculations refresh once a second by default. Set `RefreshSeconds = 0.5` for faster updates. Hidden tint skips normal support calculations, and destroyed parts stop being drawn immediately.
- Per-hit outcome diagnostics now run only with `RockSupportLogChecks` enabled.
- Benchmark reports include the worst frame alongside the median and p95.

## 1.3.0

- Faster hammer menu near many chests with AzuCraftyBoxes 1.8.19. At my castle the chest counting dropped from 182 to about 5 ms.
- Big bases: distant buildings are merged into fewer meshes, lights hidden behind walls go dark, loading near big bases is smoother, unchanged chests are not rebuilt, light flicker pauses out of view. Together up to 19 % more FPS at my castle (70 to 83 fps outside, 84 to 87 inside).
- New `Smoke` option, Off by default: no smoke from fires, about 5 % more FPS at my castle, no Smoked debuff. `Smoke = Normal` brings the game's smoke back.
- Portals: 0.5 s between trips instead of 2.
- Optional sun shadow culling, off by default.
- Tidier config: numbered sections, logs, test settings and benchmark keys in `9 - Debug`. Your old settings move over.
- New benchmark key Shift+End: measures every part against all off, results in `BepInEx/GapCloser_benchmarks.txt`.
- Hidden lights and building merging were inspired by ValheimBuildOptimization by RockBreaker (Dracoalias).

## 1.2.0

- New: rock support view. Every part of the mine rock you look at or hit is tinted by what breaking it does (blue holds others, green drops, red held by the ground, yellow held by other parts), worked out from the game's own support rule. In my tests 95 of 95 hits dropped exactly the predicted parts. Shift+O hides the tint, `gapcloser_rocksupport` switches it. The idea is from Ore Support by JereKuusela.
- New, still being tested: occlusion culling. Building pieces fully hidden behind walls are not drawn (shadows stay), on by default on DirectX 11. At my heavy castle 0.7 to 1.4 ms faster a frame. Home switches it, Shift+Home measures it. Can be switched off in the config.
- Less garbage near chests with ValheimPlus 0.10.3.0 (crafting from chests), ExtraSlots 1.2.21 and AzuCraftyBoxes 1.8.19. With the inventory open at my castle garbage went from 12.2 to 3.9 MB a second.

## 1.1.0

- Faster inventory and crafting stations near many chests with AzuCraftyBoxes 1.8.19. Chest counts are kept until a chest changes, its pull rules for a few seconds, and a debug text is built only with its debug log on. At my castle the stutters with the inventory open halved.
- Fire, smoke and portal effects of building pieces pause while no camera sees them and catch up when seen again. The castle's particle work dropped by about a third.
- The teleport log (`Debug.LogTeleports`) also writes how the 5 s after arrival went.

## 1.0.0

- First release.
- Fast portals and teleports (a 0.25 s fade, back as soon as the destination is loaded).
- The Forge of Potential no longer teaches every recipe with AzuCraftyBoxes 1.8.19.
- Recipe cleanup on entering a world (with AzuCraftyBoxes installed) and the `gapcloser_fixrecipes` console command, with a backup of the character first.
- Guards for the ValheimPlus 0.10.3.0 breeding and tame hover errors and the AzuCraftyBoxes 1.8.19 fermenter error.
- No more trigger damage warning spam from stakes, and the recipe discovery check once a frame.
- `ForceOnUntestedVersions` turns the fixes on also for game and mod versions they were not checked against.
