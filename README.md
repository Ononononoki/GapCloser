# GapCloser

Closes the gaps other mods leave in a modded Valheim. A rock support view for ore deposits, occlusion culling for big bases, portals without the wait, less garbage near chests, the AzuCraftyBoxes Forge of Potential recipe bug fixed and cleaned up, other mods' errors guarded. Each fix only for the versions it was checked against. Made for my own modpack, not a fix-all. More fixes may come for the gaps I find in the mods I play with. Bug reports and suggestions are welcome in Issues.

A Valheim mod by Onononoki. Get it on Thunderstore: https://thunderstore.io/c/valheim/p/Onononoki/GapCloser/

The full description, settings and changelog are in [package/README.md](package/README.md) and [package/CHANGELOG.md](package/CHANGELOG.md).

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
