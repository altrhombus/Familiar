# Familiar

Offline servicing for Windows images. Familiar patches WIM and ESD files with cumulative updates, adds
language packs and Features on Demand, injects drivers, removes provisioned apps, and hands the result to
Configuration Manager or packs it into an ISO.

Familiar is a C# successor to [WIM Witch](https://github.com/thenotoriousdrr/WIM-Witch), which is no
longer maintained.

> **Status:** early. Recipe-driven language builds work end to end: a Windows 11 25H2 Enterprise image
> has been built with German and Japanese (including WinRE) from the 24H2/25H2 language media.

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
- **Release knowledge is data, not code.** Releases, cumulative updates and lifecycles come from
  [DataForGeeks](https://github.com/altrhombus/DataForGeeks), cached locally with a bundled snapshot for
  offline machines. Releases that receive the same KB at the same revision share a servicing branch, so a new
  enablement release (25H2, 26H2) needs a data refresh, not a Familiar release.
- **The image is the final authority.** At build time the mounted image's Foundation package gives its true
  servicing baseline, and the language media's own package version must match it before anything is applied.
- **Media is inventoried, not listed.** Language packs, Features on Demand, fonts and WinPE components are
  discovered from the Languages and Optional Features ISO. Font capabilities come from Windows locale script
  data.
- **Configuration Manager without the console.** The AdminService (REST) is the primary transport, with
  direct WMI to the SMS Provider as a fallback for sites without AdminService configured. Neither needs
  the ConfigMgr console or PowerShell module on the build machine.
- **Updates from a local folder first.** Drop MSUs into a folder (DISM resolves checkpoint cumulative
  updates on 24H2 and later when they sit together). Microsoft Update Catalog and ConfigMgr sources come
  later.
- **The app runs unpackaged** so it can elevate, and bundles the Windows App SDK so no runtime install
  is needed. It starts unelevated and offers to restart as administrator.

## Roadmap

1. ~~Mount/dismount, index selection, export~~
2. ~~Languages: language packs, language FODs, WinRE languages, default UI language~~
3. ~~Recipes (JSON) and the CLI build command~~
4. ~~Updates: LCU, checkpoint CUs and .NET from a folder~~; still to do: Safe OS and Setup Dynamic Updates for WinRE and media
5. Languages for install media: boot.wim, `lang.ini`; re-provisioning inbox apps from the Inbox Apps ISO
6. Drivers, .NET 3.5, provisioned app removal, registry files, custom scripts
7. Configuration Manager: create/update OS image packages, distribute content, update catalog downloads
8. Update downloads from the Microsoft Update Catalog
9. ISO creation (`oscdimg` from the ADK)
10. Autopilot JSON drop-in

## Building

Requires the .NET 10 SDK on Windows. Visual Studio 2026 is optional.

```powershell
dotnet build Familiar.slnx
dotnet test --solution Familiar.slnx
```

## Using the CLI

```powershell
familiar releases                       # releases, servicing baselines, latest CUs, end of support
familiar languages F:\                  # what a Languages and Optional Features ISO offers
familiar languages F:\ --lang de-DE ja-JP
familiar recipe new my-build.json       # starter recipe to edit
familiar recipe check my-build.json     # validate recipe and media without changing anything
familiar info E:\sources\install.wim    # indexes, release, patch level, support (elevated)
familiar build my-build.json            # run the build (elevated)
```

Release data refreshes from the network every 12 hours; pass `--offline` on air-gapped machines.

Servicing commands require an elevated prompt. ISO creation and WinPE language packs require the
[Windows ADK and WinPE add-on](https://learn.microsoft.com/windows-hardware/get-started/adk-install).

## Acknowledgements

Familiar builds on what Donna Ryan worked out in WIM Witch (MIT licensed): which steps to run, in what order,
and the quirks of each Windows release. No WIM Witch code is included.

## License

[BSD 3-Clause](LICENSE)
