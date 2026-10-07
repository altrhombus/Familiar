# Roadmap

What Familiar does today, and what's planned. Items are grouped by area, not ordered by priority.

## Working today

- **Images:** export, mount, service and export again. Indexes, release, patch level and support dates via
  `familiar info`; WinRE build, languages and size with `--winre`.
- **Release data:** from [DataForGeeks](https://github.com/altrhombus/DataForGeeks), with a bundled snapshot
  for offline machines. Servicing branches are derived from shared KBs, so new enablement releases need no
  Familiar release.
- **Languages:** language packs, LIPs, language Features on Demand in Microsoft's order, script fonts,
  default UI language. Regional variants (de-CH, en-CA, es-US) get their features and use a parent language
  pack.
- **WinRE:** patched by default (cumulative update for its servicing stack, then the Safe OS Dynamic Update);
  localized only when `winRE.languages` asks.
- **Inbox apps:** apps already in the image are re-provisioned from the Inbox Apps ISO after languages.
- **Updates:** cumulative updates with checkpoints, .NET, and Safe OS downloaded from the Update Catalog and
  verified; superseded downloads pruned.
- **Configuration Manager:** create or update OS image packages, versioned source folders, templated
  name/version/description, downgrade guard, rollback, optional DP refresh. AdminService for reads, creates and
  methods; WMI for property updates and as fallback.
- **Build machine checks:** `familiar doctor` (Defender ASR, disk, network, tools, ConfigMgr reachability).
- **CLI:** single self-contained `familiar.exe`.

## Install media

- **boot.wim:** cumulative update, WinPE language packs and components, `lang.ini` (`/Gen-LangINI`), so Setup
  offers the image's languages.
- **Setup Dynamic Update:** expand into `sources`, then copy `setup.exe`, `setuphost.exe` and the boot
  manager files from the serviced boot.wim (Microsoft's media update steps 26–28).
- **ISO creation:** `oscdimg` from the Windows ADK, BIOS and UEFI boot.
- **Use the ADK's DISM** when the build machine's own DISM is older than the image being serviced.

## Image customization

- Drivers (folder of INFs, per-model sets).
- .NET Framework 3.5 (`NetFX3` after cleanup, as Microsoft's media update script does).
- Optional components and other Features on Demand.
- Provisioned app removal (a list, applied before re-provisioning).
- Registry files applied to the offline hives.
- Custom scripts at defined points in the build.
- **Autopilot JSON drop-in:** `AutopilotConfigurationFile.json` into `Windows\Provisioning\Autopilot`.

## Configuration Manager

- **Publish with other credentials** than the signed-in account.
- **ConfigMgr as an update source:** take updates from the site's software update content instead of the
  Update Catalog, for machines without internet access.
- Revisit AdminService property updates if a later ConfigMgr version implements PATCH (on 2603 it's
  `NotImplemented`, and POST replaces the whole object).

## Releases

- **Windows Server:** downloads are client-only so far. Server release data can be built in DataForGeeks when
  needed.

## App

- WinUI 3 pages over `Familiar.Core`: recipe editor, media inventory, build progress, publish. The app is a
  shell today.

## Considered, not planned

- **Local Experience Packs (.appx languages):** Windows 11 can't add them to an image (only .cab language packs
  and LIPs). Reconsider only if Microsoft documents a supported offline path.
- Dropped from WIM Witch: OneDrive refresh, Start menu layout, OSDSUS, dad jokes.
