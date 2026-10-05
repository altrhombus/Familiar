# Familiar

Offline servicing for Windows images. Familiar patches WIM and ESD files with cumulative updates, adds
language packs and Features on Demand, injects drivers, removes provisioned apps, and hands the result to
Configuration Manager or packs it into an ISO.

Familiar is a C# successor to [WIM Witch](https://github.com/thenotoriousdrr/WIM-Witch), which is no
longer maintained.

> **Status:** early scaffolding. The solution builds and the release catalog works; servicing is not
> implemented yet.

## Layout

| Project | Purpose |
| --- | --- |
| `src/Familiar.Core` | Servicing engine: DISM API, release knowledge, configuration model. No UI. |
| `src/Familiar.Cli` | `familiar.exe` for headless and bulk builds from a configuration file. |
| `src/Familiar.App` | WinUI 3 front end over `Familiar.Core`. |
| `tests/Familiar.Core.Tests` | xUnit v3 tests for the engine. |

## Design decisions

- **DISM API, not PowerShell.** Servicing goes through `dismapi.dll` via
  [Microsoft.Dism](https://github.com/jeffkl/ManagedDism), which gives real progress callbacks and
  cancellation.
- **Release knowledge is data.** `WindowsReleaseCatalog` maps builds to releases and to the language/FOD
  media they share (for example, Windows 11 25H2 uses the 24H2 media). Adding a release is one line.
- **Configuration Manager without the console.** The AdminService (REST) is the primary transport, with
  direct WMI to the SMS Provider as a fallback for sites without AdminService configured. Neither needs
  the ConfigMgr console or PowerShell module on the build machine.
- **Updates from a local folder first.** Drop MSUs into a folder (DISM resolves checkpoint cumulative
  updates on 24H2 and later when they sit together). Microsoft Update Catalog and ConfigMgr sources come
  later.
- **The app runs unpackaged** so it can elevate, and bundles the Windows App SDK so no runtime install
  is needed. It starts unelevated and offers to restart as administrator.

## Roadmap

1. Mount/dismount, index selection, export
2. Updates: SSU/LCU, checkpoint CUs, .NET, Setup Dynamic Update; boot.wim and WinRE
3. Languages: language packs, language FODs, WinPE/WinRE language packs, `lang.ini`, default UI language
4. Drivers, .NET 3.5, provisioned app removal, registry files, custom scripts
5. Saved configurations (JSON) and the CLI build command
6. Configuration Manager: create/update OS image packages, distribute content, update catalog downloads
7. Update downloads from the Microsoft Update Catalog
8. ISO creation (`oscdimg` from the ADK)
9. Autopilot JSON drop-in

## Building

Requires the .NET 10 SDK on Windows. Visual Studio 2026 is optional.

```powershell
dotnet build Familiar.slnx
dotnet test --solution Familiar.slnx
dotnet run --project src/Familiar.Cli -- releases
```

Servicing commands require an elevated prompt. ISO creation and WinPE language packs require the
[Windows ADK and WinPE add-on](https://learn.microsoft.com/windows-hardware/get-started/adk-install).

## Acknowledgements

Familiar builds on what Donna Ryan worked out in WIM Witch (MIT licensed): which steps to run, in what order,
and the quirks of each Windows release. No WIM Witch code is included.

## License

[MIT](LICENSE)
