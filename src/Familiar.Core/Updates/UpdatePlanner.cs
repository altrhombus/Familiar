using System.Globalization;
using System.Text.RegularExpressions;
using Familiar.Core.Releases;
using Familiar.Core.Servicing;

namespace Familiar.Core.Updates;

/// <summary>An update file found in an updates folder.</summary>
/// <param name="Architecture">"x64", "arm64" or "x86" from the file name, or <c>null</c> when it does not say.</param>
public sealed record UpdateFile(string Path, string Kb, string? Architecture);

/// <summary>What the planner knows about the image being updated.</summary>
/// <param name="Release">The image's release, used to look up KBs and the latest update.</param>
/// <param name="Architecture">"x64" or "arm64".</param>
/// <param name="InstalledRevision">The image's current revision (UBR), to skip updates it already has.</param>
public sealed record UpdateTarget(WindowsRelease? Release, string Architecture, int? InstalledRevision);

public static partial class UpdatePlanner
{
    /// <summary>Finds .msu and .cab update packages, keyed by the KB number in their file names.</summary>
    public static IReadOnlyList<UpdateFile> Scan(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Updates folder not found: {folder}");
        }

        return Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
            .Select(Parse)
            .OfType<UpdateFile>()
            .ToList();
    }

    internal static UpdateFile? Parse(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var kb = KbPattern().Match(name);
        if (!kb.Success)
        {
            return null;
        }

        var architecture = ArchitecturePattern().Match(name);
        return new UpdateFile(
            path,
            "KB" + kb.Groups["number"].Value,
            architecture.Success ? architecture.Groups["arch"].Value.ToLowerInvariant() : null);
    }

    /// <summary>
    /// Orders updates for offline installation. Microsoft's documented offline method for checkpoint cumulative
    /// updates is to install each MSU individually in order, so Windows updates known to the release data go in
    /// release-date order (checkpoints first), followed by anything else (for example .NET cumulative updates).
    /// </summary>
    public static ServicingPlan Plan(IEnumerable<UpdateFile> files, UpdateTarget target, ReleaseCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(target);

        var warnings = new List<string>();
        var known = new List<(UpdateFile File, WindowsUpdate Update)>();
        var unknown = new List<UpdateFile>();

        var branchUpdates = catalog is null || target.Release is null
            ? []
            : catalog.Updates
                .Where(u => u.OsType == target.Release.OsType && catalog.Releases.Any(r =>
                    r.OsType == u.OsType && r.MajorVersion == u.MajorVersion
                    && string.Equals(r.Version, u.WindowsVersion, StringComparison.OrdinalIgnoreCase)
                    && r.ServicingBaselineBuild == target.Release.ServicingBaselineBuild))
                .ToList();

        foreach (var file in files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (file.Architecture is not null && !file.Architecture.Equals(target.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Skipping {System.IO.Path.GetFileName(file.Path)}: it is for {file.Architecture}, the image is {target.Architecture}.");
                continue;
            }

            // Prefer the entry for the image's own release; enablement releases share KBs with their baseline.
            var update = branchUpdates
                .Where(u => u.KbArticle.Equals(file.Kb, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(u => target.Release is not null && u.Build == target.Release.Build)
                .FirstOrDefault();

            if (update is null)
            {
                unknown.Add(file);
                continue;
            }

            if (!update.CanSlipstream)
            {
                warnings.Add($"Skipping {file.Kb}: {update.Type} updates cannot be added to an offline image.");
                continue;
            }

            if (target.InstalledRevision is { } installed && update.Revision <= installed)
            {
                warnings.Add($"Skipping {file.Kb}: the image is already at revision {installed}.");
                continue;
            }

            known.Add((file, update));
        }

        var actions = new List<ServicingAction>();
        foreach (var (file, update) in known.OrderBy(k => k.Update.ReleaseDate).ThenBy(k => k.Update.Revision))
        {
            actions.Add(new AddPackageAction(file.Path, $"{update.KbArticle} cumulative update ({update.Build}.{update.Revision}, {update.ReleaseDate:yyyy-MM-dd})"));
        }

        // .NET cumulative updates (file names carry "ndp", e.g. -ndp481_) supersede each other but are not in the
        // release data, so keep only the newest; KB numbers rise over time.
        var dotNet = unknown.Where(IsDotNet).ToList();
        foreach (var stale in dotNet.OrderByDescending(f => KbNumber(f.Kb)).Skip(1))
        {
            warnings.Add($"Skipping {stale.Kb}: a newer .NET cumulative update is in the folder.");
            unknown.Remove(stale);
        }

        foreach (var file in unknown.OrderBy(f => KbNumber(f.Kb)))
        {
            if (branchUpdates.Count > 0)
            {
                warnings.Add($"{file.Kb} is not a Windows cumulative update in the release data; applying it after them (expected for .NET updates).");
            }

            actions.Add(new AddPackageAction(file.Path, $"{file.Kb}"));
        }

        if (catalog is not null && target.Release is not null && catalog.GetLatestUpdate(target.Release) is { } latest
            && (target.InstalledRevision is null || latest.Revision > target.InstalledRevision)
            && !known.Any(k => k.Update.Revision >= latest.Revision))
        {
            warnings.Add($"The latest update for {target.Release.Name} is {latest}, which is not in the updates folder.");
        }

        return new ServicingPlan(actions, warnings, []);
    }

    private static bool IsDotNet(UpdateFile file) =>
        System.IO.Path.GetFileName(file.Path).Contains("-ndp", StringComparison.OrdinalIgnoreCase);

    private static int KbNumber(string kb) =>
        int.TryParse(kb.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue;

    // windows11.0-kb5043080-x64_953449672073f8fb99badb4cc6d5d7849b9c83e8.msu
    [GeneratedRegex(@"kb(?<number>\d{6,8})", RegexOptions.IgnoreCase)]
    private static partial Regex KbPattern();

    [GeneratedRegex(@"[-_](?<arch>x64|arm64|x86)[-_.]", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitecturePattern();
}
