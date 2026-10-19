# GapCloser

Fixes and speed-ups for Valheim and the mods I play with: a faster hammer menu and inventory near many chests with AzuCraftyBoxes, occlusion culling and merged buildings for big bases, fast portals, a rock support view for ore deposits, smoke off for more FPS, other mods' errors guarded. Each fix only for the versions it was checked against. Made for my own modpack, not a fix-all. More fixes may come for the gaps I find in the mods I play with. Bug reports and suggestions are welcome in Issues.

A Valheim mod by Onononoki. Get it on Thunderstore: https://thunderstore.io/c/valheim/p/Onononoki/GapCloser/

The full description, settings and changelog are in [package/README.md](package/README.md) and [package/CHANGELOG.md](package/CHANGELOG.md).

The rock support view has a configurable range, linger time, and refresh interval. It stays highlighted for five seconds after looking away by default. Set `ShowSeconds = 0` for immediate hiding. The hover tooltip shows the configured toggle key.

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
