# GapCloser

Fixes and speed-ups for Valheim and the mods I play with: a faster hammer menu and inventory near many chests with AzuCraftyBoxes, occlusion culling and merged buildings for big bases, fast portals, a rock support view for ore deposits, smoke off for more FPS, other mods' errors guarded. Fixes for other mods check the code they rely on, so new versions of those mods keep them on while that code is unchanged. Made for my own modpack, not a fix-all. More fixes may come for the gaps I find in the mods I play with. Bug reports and suggestions are welcome in Issues.

A Valheim mod by Onononoki. Get it on Thunderstore: https://thunderstore.io/c/valheim/p/Onononoki/GapCloser/

The full description, settings and changelog are in [package/README.md](package/README.md) and [package/CHANGELOG.md](package/CHANGELOG.md).

The rock support view has a configurable range, linger time, and refresh interval. It stays highlighted for five seconds after looking away by default. Set `ShowSeconds = 0` for immediate hiding. The hover tooltip shows the configured toggle key.

With `WishboneForgeWarning` enabled in `2 - Options`, an equipped Wishbone pulses once a second while the selected item's next attempt at the Forge of Potential will destroy it. The refinement button glows with each pulse and the Wishbone's own ping effect plays, its sound at 30% of the normal volume. While the upgrade of such an item is running, the pulse gets faster (about three times a second) on the progress bar until you cancel or it finishes. Hover over the button to read the warning. Carrying an unequipped Wishbone does not activate it. The preview reads the character's current upgrade seed without consuming an attempt or changing the game's random state. A missing pulse does not guarantee a safe attempt. The preview turns itself off when the game's upgrade code differs from the checked code or another mod patches the roll, and the BepInEx log says so.

## Bug reports and suggestions

Bug reports and suggestions for gaps to close: open an issue at https://github.com/Ononononoki/GapCloser/issues. If something breaks, please add your mod list and the BepInEx log (`BepInEx/LogOutput.log`).

## Building

Needs the .NET SDK, Valheim and a mod manager profile with BepInEx.

```
dotnet build -c Release -p:GamePath="<Valheim folder>" -p:ProfilePath="<mod manager profile folder>"
```

The dll is built into `bin/Release/net472` and copied to `package/`.

## License

MIT, see [LICENSE](LICENSE).
