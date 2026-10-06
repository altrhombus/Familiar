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
- **Configuration Manager without the console.** Builds publish straight to an OS image package: copy to the
  package source, create or update, reload image properties, distribute or refresh. The AdminService (REST)
  handles reads, creates and method calls; property updates (such as repointing a package) go over WMI,
  because the AdminService cannot update properties (tested on 2603). WMI is also the fallback when the
  AdminService is unreachable. Neither needs the ConfigMgr console or PowerShell module on the build machine. Updating an existing package changes what its task sequences
  deploy, so point recipes at a test package first: the replaced WIM is kept as `.previous` for
  `familiar configmgr rollback`, and `refreshDistributionPoints: false` holds the new image back from DPs.
  Sites that keep a folder per build can set `sourceFolderName` (e.g. `{release}_{arch}_{date:yyMM}_{n}`):
  each publish gets a new folder, the package is repointed, and rollback points it back.
- **Inbox apps follow the languages.** Apps already in the image are re-provisioned from the Inbox Apps ISO so
  their bundles add resources for newly added languages. Dependencies come from each app's own manifest.
- **Updates downloaded for you.** The latest cumulative update comes from the release data; Familiar finds it
  in the Microsoft Update Catalog, downloads every file it lists (including checkpoint updates), verifies each
  against the catalog's SHA-1, and installs them oldest first after languages. The catalog has no API, so its
  page parsing is isolated in `UpdateCatalogClient`. A plain folder of MSUs works too.
- **The app runs unpackaged** so it can elevate, and bundles the Windows App SDK so no runtime install
  is needed. It starts unelevated and offers to restart as administrator.

## Roadmap

1. ~~Mount/dismount, index selection, export~~
2. ~~Languages: language packs, language FODs, WinRE languages, default UI language~~
3. ~~Recipes (JSON) and the CLI build command~~
4. ~~Updates: LCU, checkpoint CUs and .NET from a folder; WinRE patched with the Safe OS Dynamic Update~~; still to do: Setup Dynamic Update for install media
5. ~~Re-provisioning inbox apps from the Inbox Apps ISO~~; still to do: boot.wim and `lang.ini` for install media
6. Drivers, .NET 3.5, provisioned app removal, registry files, custom scripts
7. ~~Configuration Manager: create/update OS image packages, distribute content~~ (needs a run against a real site); still to do: ConfigMgr as an update source
8. ~~Update downloads from the Microsoft Update Catalog~~
9. ISO creation (`oscdimg` from the ADK)
10. Autopilot JSON drop-in
11. Local Experience Packs (.appx languages): Windows 11 cannot image them (only .cab language packs and LIPs), so consider only if Microsoft documents a supported path

## Building

Requires the .NET 10 SDK on Windows. Visual Studio 2026 is optional. To run Familiar on a build machine without
.NET, publish the self-contained CLI with `./eng/Publish-Cli.ps1`; see [Setting up a build machine](docs/build-machine-setup.md).

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
familiar updates list --release 25H2    # latest cumulative and .NET updates in the catalog
familiar updates download --release 25H2 --folder C:\Updates\25H2
familiar inbox-apps G:\                 # apps on an Inbox Apps ISO and their dependencies
familiar configmgr info --site-server cm01.contoso.com
familiar configmgr publish my-build.json   # publish an existing build without rebuilding
familiar info E:\sources\install.wim    # indexes, release, patch level, support (elevated)
familiar doctor --recipe my-build.json  # check this machine (Defender, disk, network, ConfigMgr) before building
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
