# Setting up a build machine

> **Draft.** Sections 1–4 and 7 are verified on a clean Windows Server 2025 VM. Steps marked _(to verify)_ are
> still assumptions; the domain-join and publishing steps are next.

A build machine mounts Windows images, services them with DISM, and (optionally) publishes them to
Configuration Manager. It needs very little installed, but its **environment** matters: Defender policy, disk
space, network access, and the account it runs as cause more failed builds than anything missing.

`familiar doctor` checks all of this. Run it after each step below and fix what it reports.

## 1. The machine

| | Recommendation |
| --- | --- |
| OS | Windows Server 2025 or Windows 11, fully updated. Desktop Experience if you'll use the app. |
| CPU / memory | 4+ cores, 8–16 GB. Servicing is mostly disk-bound. |
| OS disk | 80 GB |
| Data disk | 150 GB+, NTFS, for the working directory, media and downloaded updates. A multi-language build with a cumulative update peaks near 100 GB. |
| Domain | Joined, if you publish to ConfigMgr (Kerberos to the SMS Provider and the package source share). |

A virtual machine works. Verified baseline: a clean Windows Server 2025 Standard VM (build 26100.33438) on
Hyper-V, with nothing installed, passes every machine check in `familiar doctor` (elevated). DISM,
`dism.exe` and `expand.exe` ship with Windows, and its DISM (26100) services 25H2 images, which share the
24H2 servicing baseline.

Keep the host's DISM recent: the build machine's DISM services images up to its own release reliably. For a
newer Windows release than the host, install the [Windows ADK](https://learn.microsoft.com/windows-hardware/get-started/adk-install)
Deployment Tools _(Familiar does not use the ADK's DISM yet)_.

## 2. Defender

Two attack surface reduction rules interfere with offline servicing:

| Rule | Effect in block mode |
| --- | --- |
| Block abuse of exploited vulnerable signed drivers (`56a863a9…`) | Blocks driver files inside cumulative updates (seen with `ksld.sys` in the 2026-09 update), so the update rolls back with `0x800f0922`. |
| Block use of copied or impersonated system tools (`c0033c00…`) | DISM runs `DismHost.exe` from its scratch folder, which this rule treats as a copied system tool. |

Ask whoever manages Defender policy to put both in **audit** mode for build machines. `familiar doctor` reports
each rule's mode where it can read it, and any blocks it recorded in the last 30 days. When a build fails because
of a block, Familiar names the rule and file in the error.

Real-time scanning of mount and scratch folders slows servicing considerably. Whether to exclude the working
directory is a security decision; discuss it with the same people.

## 3. Install Familiar

Familiar is one self-contained file; no .NET installation is needed.

1. On a development machine, build it: `./eng/Publish-Cli.ps1` → `artifacts\familiar-win-x64\familiar.exe`.
2. Copy it to the build machine, for example `D:\Familiar\familiar.exe`, and optionally add that folder to `PATH`.
3. From an elevated prompt, run `familiar doctor`. On a clean machine every check should pass; anything else is
   environment (Defender policy, disk, network) rather than something to install.

## 4. Media

Download from the Volume Licensing Service Center (or Visual Studio subscriptions):

- Windows installation media (for example *Windows 11, version 25H2*)
- *Languages and Optional Features* for the matching servicing baseline (24H2/25H2 share one)
- *Inbox Apps* for the release

Either attach the ISOs to the machine (DVD drives on a VM, or *Mount* on a physical machine) or copy their
contents to the data disk. Attached ISOs are simplest; copied folders keep their paths if drive letters change.
Verified layout on the walkthrough VM:

```text
E:\                                      Windows 11 25H2 install ISO
F:\                                      Languages and Optional Features ISO
G:\                                      Inbox Apps ISO
I:\Familiar\familiar.exe
I:\Familiar\Recipes\
I:\Familiar\Updates\Windows11-25H2-x64\   (downloaded updates; kept between builds)
I:\Familiar\Work\                         (workingDirectory; emptied after each build)
I:\Familiar\Output\
```

## 5. Account

Builds need an **elevated** prompt (the DISM API refuses to start otherwise). Publishing to ConfigMgr also needs
an account that is an administrative user in Configuration Manager with rights to create and modify OS image
packages and distribute content, and write access to the package source share.

The simplest arrangement is to sign in to the build machine as that account and use *Run as administrator*.
_(Running elevated as a different account than the signed-in one: to verify.)_

Before the machine is domain-joined, `familiar doctor --recipe` reports the ConfigMgr checks like this, which is
expected:

```text
ConfigMgr
  [WARN] Administration service: HTTPS certificate not trusted by this machine.
  [FAIL] WMI (SMS Provider): Could not reach the SMS Provider on <server> over WMI: Access is denied.
```

- **Certificate not trusted**: the machine doesn't trust the SMS Provider's HTTPS certificate. Joining the domain
  fixes this when the certificate comes from your enterprise PKI. If the site uses ConfigMgr's self-signed
  certificate, import it into the machine's Trusted Root store, or let Familiar use WMI.
- **Access is denied** (WMI): the signed-in account has no rights on the site. Join the domain and sign in as the
  ConfigMgr account.

## 6. Network

| Destination | Why | Without it |
| --- | --- | --- |
| `raw.githubusercontent.com` | Release data (DataForGeeks) | Familiar uses its built-in copy; pass `--offline`. |
| `www.catalog.update.microsoft.com` | Finding updates | Copy MSUs into the updates folder yourself and set `download: false`. |
| `catalog.sf.dl.delivery.mp.microsoft.com` | Downloading updates | As above. |
| SMS Provider, HTTPS 443 | Administration service (reads, creates, distribution) | Familiar falls back to WMI. |
| SMS Provider, RPC/DCOM | WMI (repointing existing packages) | Updating existing packages fails. |
| Package source share, SMB 445 | Copying images | Publishing fails. |

## 7. Check and build

```powershell
familiar doctor --recipe D:\Familiar\Recipes\25h2-production.json
familiar recipe check D:\Familiar\Recipes\25h2-production.json
familiar build D:\Familiar\Recipes\25h2-production.json --dism-log D:\Familiar\Work\dism.log
familiar info D:\Familiar\Output\Windows11-25H2-Enterprise.wim
```

On the walkthrough VM, a 25H2 Enterprise build with 16 languages, 46 inbox apps, the latest cumulative update
and the .NET update took about **70 minutes**; the cumulative update alone took about 25.

Languages added to WinRE make it larger. The build logs WinRE's size before and after, and warns when it grew;
check it against the recovery partition your task sequence creates, or recovery is disabled on deployed devices.

`info` should show the latest cumulative update before you publish. Publishing refuses an image older than the
package's current build unless the recipe sets `allowOlderImage`.
