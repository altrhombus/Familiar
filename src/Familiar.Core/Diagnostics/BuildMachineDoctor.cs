using System.Diagnostics;
using System.Management;
using Familiar.Core.ConfigMgr;
using Familiar.Core.Recipes;
using Microsoft.Win32;

namespace Familiar.Core.Diagnostics;

public enum CheckStatus
{
    Pass,
    Info,
    Warn,
    Fail,
}

/// <param name="Fix">What to do about a warning or failure.</param>
public sealed record CheckResult(string Area, string Name, CheckStatus Status, string Detail, string? Fix = null);

/// <summary>
/// Checks that a machine can build images: the prerequisites a clean install lacks and the environment problems
/// (Defender rules, disk space, network, ConfigMgr access) that otherwise only show up an hour into a build.
/// </summary>
public sealed class BuildMachineDoctor(HttpClient http)
{
    /// <summary>Peak working-directory use for a multi-language build with a cumulative update.</summary>
    private const long RecommendedFreeBytes = 100L << 30;
    private const long MinimumFreeBytes = 40L << 30;

    private static readonly (Guid Id, string Name, string Effect)[] AsrRulesThatBreakServicing =
    [
        (new Guid("56a863a9-875e-4185-98a7-b882c64b5ce5"), "Block abuse of exploited vulnerable signed drivers",
            "blocks driver files inside cumulative updates (seen with ksld.sys), so updates roll back with 0x800f0922"),
        (new Guid("c0033c00-d16d-4114-a5a0-dc9b3a7d2ceb"), "Block use of copied or impersonated system tools",
            "DISM runs DismHost.exe from its scratch folder, which this rule treats as a copied system tool"),
    ];

    public async Task<IReadOnlyList<CheckResult>> RunAsync(BuildRecipe? recipe, CancellationToken cancellationToken = default)
    {
        var results = new List<CheckResult>();
        CheckSystem(results);
        CheckTools(results);
        CheckDisk(results, recipe?.WorkingDirectory ?? @"C:\FamiliarWork");
        CheckDefender(results);
        await CheckNetworkAsync(results, recipe, cancellationToken).ConfigureAwait(false);

        if (recipe is not null)
        {
            CheckRecipe(results, recipe);
            if (recipe.ConfigMgr is { } configMgr)
            {
                await CheckConfigMgrAsync(results, configMgr, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    private static void CheckSystem(List<CheckResult> results)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        // ProductName still says "Windows 10" on Windows 11; the build number is authoritative.
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        if (Environment.OSVersion.Version.Build >= 22000 && !product.Contains("Server", StringComparison.OrdinalIgnoreCase))
        {
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        var display = key?.GetValue("DisplayVersion") as string;
        var build = $"{Environment.OSVersion.Version.Build}.{key?.GetValue("UBR")}";
        results.Add(new("System", "Operating system", CheckStatus.Info, $"{product} {display} (build {build})"));

        results.Add(ProcessElevation.IsElevated
            ? new("System", "Elevation", CheckStatus.Pass, "Running as administrator")
            : new("System", "Elevation", CheckStatus.Fail, "Not running as administrator; the DISM API refuses to start.",
                "Run from an elevated prompt (Run as administrator), as the account that has ConfigMgr rights if you publish."));
    }

    private static void CheckTools(List<CheckResult> results)
    {
        var system = Environment.SystemDirectory;
        var dismApi = Path.Combine(system, "dismapi.dll");
        if (File.Exists(dismApi))
        {
            var version = FileVersionInfo.GetVersionInfo(dismApi).ProductVersion;
            results.Add(new("Tools", "DISM", CheckStatus.Pass,
                $"dismapi.dll {version}. Images newer than this host's release may need the ADK's DISM."));
        }
        else
        {
            results.Add(new("Tools", "DISM", CheckStatus.Fail, "dismapi.dll not found.", "This Windows installation is missing DISM."));
        }

        foreach (var tool in new[] { "dism.exe", "expand.exe" })
        {
            results.Add(File.Exists(Path.Combine(system, tool))
                ? new("Tools", tool, CheckStatus.Pass, "Present")
                : new("Tools", tool, CheckStatus.Fail, "Not found in System32."));
        }

        var oscdimg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe");
        results.Add(File.Exists(oscdimg)
            ? new("Tools", "Windows ADK", CheckStatus.Pass, "Deployment Tools installed (oscdimg)")
            : new("Tools", "Windows ADK", CheckStatus.Info, "Not installed. Not needed for WIM builds; ISO creation (roadmap) will need its Deployment Tools."));
    }

    private static void CheckDisk(List<CheckResult> results, string workingDirectory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(workingDirectory));
        if (root is null || !new DriveInfo(root).IsReady)
        {
            results.Add(new("Disk", "Working directory", CheckStatus.Fail, $"{workingDirectory} is not on a ready drive."));
            return;
        }

        var drive = new DriveInfo(root);
        var free = drive.AvailableFreeSpace;
        var detail = $"{workingDirectory} on {drive.Name} ({drive.DriveFormat}): {free >> 30} GB free";
        results.Add(free switch
        {
            < MinimumFreeBytes => new("Disk", "Working directory", CheckStatus.Fail, detail,
                $"A build needs about {RecommendedFreeBytes >> 30} GB at its peak (working copy, mount, update scratch, export). Free space or move workingDirectory."),
            < RecommendedFreeBytes => new("Disk", "Working directory", CheckStatus.Warn, detail,
                $"Multi-language builds with a cumulative update can peak near {RecommendedFreeBytes >> 30} GB."),
            _ => new("Disk", "Working directory", CheckStatus.Pass, detail),
        });

        if (drive.DriveFormat is not "NTFS" and not "ReFS")
        {
            results.Add(new("Disk", "File system", CheckStatus.Fail, $"{drive.Name} is {drive.DriveFormat}; DISM mounts need NTFS."));
        }
    }

    private static void CheckDefender(List<CheckResult> results)
    {
        // Settings come from several places (local preference, Group Policy, Intune/Defender for Endpoint) and some
        // need elevation to read; the event log shows what the rules actually did and is readable by anyone.
        var configured = ReadAsrActions(out var readable);
        var history = ReadAsrHistory(TimeSpan.FromDays(30));

        foreach (var (id, name, effect) in AsrRulesThatBreakServicing)
        {
            var action = configured.GetValueOrDefault(id);
            var (blocks, audits) = history.GetValueOrDefault(id);
            var seen = blocks > 0 ? $" It blocked {blocks} file operation(s) on this machine in the last 30 days."
                : audits > 0 ? $" Audit events in the last 30 days: {audits}." : string.Empty;

            results.Add((action, blocks) switch
            {
                (1, _) or (_, > 0) => new("Defender", name, CheckStatus.Fail, $"{(action == 1 ? "Block mode" : "Blocking")}: {effect}.{seen}",
                    "Ask whoever manages Defender policy to put this rule in audit mode on build machines (or exclude the working directory, if the rule honors exclusions)."),
                (6, _) => new("Defender", name, CheckStatus.Warn, $"Warn mode: {effect} unless someone allows it.{seen}"),
                (2, _) => new("Defender", name, CheckStatus.Pass, $"Audit mode: logged, not blocked.{seen}"),
                _ when readable => new("Defender", name, CheckStatus.Pass, $"Not configured on this machine.{seen}"),
                _ => new("Defender", name, CheckStatus.Info, $"Could not read Defender policy without elevation; no blocks in the last 30 days.{seen}",
                    "Run doctor elevated to read the configured rule mode."),
            });
        }
    }

    /// <summary>ASR rule actions from local preferences and policy (0 off, 1 block, 2 audit, 6 warn).</summary>
    private static Dictionary<Guid, int> ReadAsrActions(out bool readable)
    {
        var actions = new Dictionary<Guid, int>();
        readable = false;

        void Merge(Guid id, int action)
        {
            // Block outranks warn outranks audit when sources disagree.
            static int Rank(int a) => a switch { 1 => 3, 6 => 2, 2 => 1, _ => 0 };
            if (!actions.TryGetValue(id, out var existing) || Rank(action) > Rank(existing))
            {
                actions[id] = action;
            }
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpPreference");
            foreach (var preference in searcher.Get().Cast<ManagementObject>())
            {
                using (preference)
                {
                    var ids = preference["AttackSurfaceReductionRules_Ids"] as string[] ?? [];
                    var values = preference["AttackSurfaceReductionRules_Actions"] as byte[] ?? [];
                    foreach (var (id, value) in ids.Zip(values))
                    {
                        if (Guid.TryParse(id, out var guid))
                        {
                            Merge(guid, value);
                        }
                    }

                    readable |= ProcessElevation.IsElevated;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }

        // Group Policy: one value per rule GUID.
        try
        {
            using var gpo = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\ASR\Rules");
            foreach (var name in gpo?.GetValueNames() ?? [])
            {
                if (Guid.TryParse(name, out var guid) && int.TryParse(gpo!.GetValue(name)?.ToString(), out var value))
                {
                    Merge(guid, value);
                }
            }

            // Intune / Defender for Endpoint: "guid=1|guid=2".
            using var mdm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows Defender\Policy Manager");
            if (mdm?.GetValue("ASRRules") is string rules)
            {
                foreach (var pair in rules.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=');
                    if (parts.Length == 2 && Guid.TryParse(parts[0], out var guid) && int.TryParse(parts[1], out var value))
                    {
                        Merge(guid, value);
                    }
                }
            }

            readable |= mdm is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        return actions;
    }

    /// <summary>Counts of block (1121) and audit (1122) events per rule.</summary>
    private static Dictionary<Guid, (int Blocks, int Audits)> ReadAsrHistory(TimeSpan window)
    {
        var history = new Dictionary<Guid, (int Blocks, int Audits)>();
        var since = DateTime.UtcNow - window;
        var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
            "Microsoft-Windows-Windows Defender/Operational",
            System.Diagnostics.Eventing.Reader.PathType.LogName,
            $"*[System[(EventID=1121 or EventID=1122) and TimeCreated[@SystemTime>='{since:o}']]]");

        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (AsrBlocks.Parse(record.ToXml()) is { } entry)
                    {
                        var counts = history.GetValueOrDefault(entry.RuleId);
                        history[entry.RuleId] = record.Id == 1121 ? (counts.Blocks + 1, counts.Audits) : (counts.Blocks, counts.Audits + 1);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException)
        {
        }

        return history;
    }

    private async Task CheckNetworkAsync(List<CheckResult> results, BuildRecipe? recipe, CancellationToken cancellationToken)
    {
        await ProbeAsync(results, "Release data (DataForGeeks)",
            new Uri("https://raw.githubusercontent.com/altrhombus/DataForGeeks/main/content/ms/win/releases.json"),
            "Needed to identify releases and latest updates; without it Familiar uses its built-in copy (use --offline).",
            cancellationToken).ConfigureAwait(false);

        if (recipe?.Updates is null or { Download: true })
        {
            await ProbeAsync(results, "Microsoft Update Catalog",
                new Uri("https://www.catalog.update.microsoft.com/Search.aspx?q=KB5129195"),
                "Needed for updates.download; otherwise copy MSUs into the updates folder by hand.", cancellationToken).ConfigureAwait(false);
            await ProbeAsync(results, "Update downloads (catalog.sf.dl.delivery.mp.microsoft.com)",
                new Uri("https://catalog.sf.dl.delivery.mp.microsoft.com/"),
                "Update files download from this host; allow it through the proxy or firewall.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProbeAsync(List<CheckResult> results, string name, Uri uri, string whenFailing, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            // Any HTTP answer proves the host is reachable; the probe URLs are not all real documents.
            results.Add(new("Network", name, CheckStatus.Pass, $"Reachable (HTTP {(int)response.StatusCode})"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            results.Add(new("Network", name, CheckStatus.Warn, $"Not reachable: {ex.Message}", whenFailing));
        }
    }

    private static void CheckRecipe(List<CheckResult> results, BuildRecipe recipe)
    {
        var problems = RecipeValidator.Validate(recipe);
        results.Add(problems.Count == 0
            ? new("Recipe", "Validation", CheckStatus.Pass, "Recipe and media paths check out")
            : new("Recipe", "Validation", CheckStatus.Fail, string.Join("; ", problems)));

        if (recipe.Updates is { Download: false } updates && Directory.Exists(updates.Folder) && !Directory.EnumerateFiles(updates.Folder, "*.msu").Any())
        {
            results.Add(new("Recipe", "Updates folder", CheckStatus.Warn, $"{updates.Folder} has no .msu files and download is off."));
        }
    }

    private static async Task CheckConfigMgrAsync(List<CheckResult> results, ConfigMgrSection section, CancellationToken cancellationToken)
    {
        AdminServiceConnection? adminService = null;
        try
        {
            if (section.Transport != ConfigMgrTransport.Wmi)
            {
                try
                {
                    adminService = await AdminServiceConnection.ConnectAsync(AdminServiceConnection.CreateClient(section.SiteServer), section.SiteCode, ownsClient: true, cancellationToken).ConfigureAwait(false);
                    results.Add(new("ConfigMgr", "Administration service", CheckStatus.Pass, $"Site {adminService.SiteCode} answers on https://{section.SiteServer}/AdminService"));
                }
                catch (Exception ex) when (ex is HttpRequestException or ConfigMgrException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    var certificate = ex.InnerException is System.Security.Authentication.AuthenticationException;
                    var denied = ex.Message.Contains("access denied", StringComparison.OrdinalIgnoreCase);
                    results.Add(new("ConfigMgr", "Administration service", section.Transport == ConfigMgrTransport.AdminService ? CheckStatus.Fail : CheckStatus.Warn,
                        certificate ? "HTTPS certificate not trusted by this machine." : ex.Message,
                        denied ? "This account has no ConfigMgr rights; run as an administrative user in Configuration Manager."
                        : certificate ? "Trust the SMS Provider's certificate, or rely on WMI."
                        : "Port 443 to the SMS Provider must be open."));
                }
            }

            // Repointing packages always goes over WMI, so check it even when the administration service works.
            if (section.Transport != ConfigMgrTransport.AdminService)
            {
                try
                {
                    await using var wmi = await WmiConnection.ConnectAsync(section.SiteServer, section.SiteCode ?? adminService?.SiteCode, cancellationToken).ConfigureAwait(false);
                    results.Add(new("ConfigMgr", "WMI (SMS Provider)", CheckStatus.Pass, $"Site {wmi.SiteCode} answers over DCOM"));
                }
                catch (ConfigMgrException ex)
                {
                    results.Add(new("ConfigMgr", "WMI (SMS Provider)", CheckStatus.Fail, ex.Message,
                        "Updating an existing package needs WMI: allow RPC/DCOM to the SMS Provider and use an account with ConfigMgr rights."));
                }
            }

            if (adminService is not null && section.PackageId is { } packageId)
            {
                var package = await adminService.GetImagePackageAsync(packageId, cancellationToken).ConfigureAwait(false);
                if (package is null)
                {
                    results.Add(new("ConfigMgr", $"Package {packageId}", CheckStatus.Fail, "Not found on the site."));
                }
                else
                {
                    results.Add(new("ConfigMgr", $"Package {packageId}", CheckStatus.Pass,
                        $"{package.Name}, build {package.ImageOSVersion}, source {package.SourcePath}"));
                    CheckShare(results, Path.GetDirectoryName(Path.GetDirectoryName(package.SourcePath)));
                }
            }
            else if (section.PackageSourcePath is { } sourcePath)
            {
                CheckShare(results, sourcePath);
            }
        }
        finally
        {
            if (adminService is not null)
            {
                await adminService.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Read access only: writing a test file to a production share is not a doctor's job.
    private static void CheckShare(List<CheckResult> results, string? folder)
    {
        if (folder is null)
        {
            return;
        }

        results.Add(Directory.Exists(folder)
            ? new("ConfigMgr", "Package source share", CheckStatus.Pass, $"{folder} is reachable (write access is checked by the first publish)")
            : new("ConfigMgr", "Package source share", CheckStatus.Fail, $"{folder} is not reachable.", "Check the share path and that this account can reach it over SMB."));
    }
}
