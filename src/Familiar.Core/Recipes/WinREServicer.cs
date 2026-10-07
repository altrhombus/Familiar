using Familiar.Core.Languages;
using Familiar.Core.Releases;
using Familiar.Core.Servicing;
using Familiar.Core.Updates;

namespace Familiar.Core.Recipes;

/// <summary>What a build does to WinRE.</summary>
/// <param name="Updates">Cumulative updates for WinRE's servicing stack; applied only along with <paramref name="SafeOS"/>.</param>
/// <param name="SafeOS">The Safe OS Dynamic Update .cab, or null to leave WinRE unpatched.</param>
internal sealed record WinREWork(
    IReadOnlyList<string> Languages,
    LanguageMedia? Media,
    string? DefaultLanguage,
    IReadOnlyList<UpdateFile>? Updates,
    string? SafeOS)
{
    public bool HasWork => SafeOS is not null || (Languages.Count > 0 && Media is not null);
}

/// <summary>Patches and localizes the WinRE inside a mounted image, during a build.</summary>
internal sealed class WinREServicer(
    IImageServicer images,
    ReleaseCatalog? catalog,
    Func<string, CancellationToken, Task<PackageIdentity?>> readCabIdentity)
{
    /// <summary>
    /// Patches and/or localizes WinRE in Microsoft's documented order: the cumulative update (for its servicing
    /// stack), languages, the Safe OS Dynamic Update, cleanup, export.
    /// </summary>
    public async Task<IReadOnlyList<string>> ServiceAsync(
        IMountedImage image,
        WinREWork plan,
        WindowsRelease? release,
        string architecture,
        string work,
        bool cleanup,
        IProgress<ServicingProgress>? progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var winRE = Path.Combine(image.MountPath, "Windows", "System32", "Recovery", "Winre.wim");
        if (!File.Exists(winRE))
        {
            return ["The image has no WinRE (Windows\\System32\\Recovery\\Winre.wim); recovery was not patched or localized."];
        }

        var workWinRE = Path.Combine(work, "winre.wim");
        File.Copy(winRE, workWinRE);
        File.SetAttributes(workWinRE, FileAttributes.Normal);

        var original = WinREVersion(workWinRE);
        log($"WinRE: build {original.Build}.{original.Revision}");

        var warnings = new List<string>();
        var changed = false;
        await using (var recovery = await images.MountAsync(workWinRE, 1, Path.Combine(work, "winre-mount"), progress, cancellationToken).ConfigureAwait(false))
        {
            if (plan.SafeOS is not null && plan.Updates is { } updates)
            {
                var updatePlan = UpdatePlanner.Plan(updates, new UpdateTarget(release, architecture, original.Revision), catalog, cumulativeOnly: true);
                log($"Updating WinRE's servicing stack ({updatePlan.Actions.Count} updates)");
                var scratch = Path.Combine(work, "scratch");
                foreach (var action in updatePlan.Actions.OfType<AddPackageAction>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log($"  {action.Description}");
                    var result = await recovery.AddUpdateAsync(action.PackagePath, scratch, $"WinRE: {action.Description}", progress, cancellationToken).ConfigureAwait(false);
                    if (result.ExitCode == CombinedUpdateKnownIssue)
                    {
                        // Microsoft's media update guidance calls this a known issue with combined cumulative updates, safe to ignore.
                        log("    0x8007007e: known issue with combined cumulative updates in WinRE; continuing");
                    }
                    else if (!result.Succeeded)
                    {
                        throw new BuildException($"dism.exe failed with exit code {result.ExitCode} adding {action.Description} to WinRE:{Environment.NewLine}{result.Output.Trim()}");
                    }

                    changed = true;
                }
            }

            if (plan.Languages.Count > 0 && plan.Media is { } media)
            {
                var languagePlan = LanguagePlanner.PlanWinPE(plan.Languages, media, recovery.GetPackages().Select(p => p.ToString()).ToList());
                warnings.AddRange(languagePlan.Warnings);

                // One line per language: each brings a language pack plus a package per installed WinPE component.
                log($"Localizing WinRE ({languagePlan.Actions.Count} packages)");
                string? current = null;
                foreach (var action in languagePlan.Actions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tag = action.Description[(action.Description.LastIndexOf(' ') + 1)..];
                    if (tag != current)
                    {
                        current = tag;
                        log($"  {tag}: {languagePlan.Actions.Count(a => a.Description.EndsWith(" " + tag, StringComparison.Ordinal))} packages");
                    }

                    await recovery.ApplyAsync(action, progress, cancellationToken).ConfigureAwait(false);
                    changed = true;
                }

                if (plan.DefaultLanguage is { } defaultLanguage
                    && plan.Languages.Contains(defaultLanguage, StringComparer.OrdinalIgnoreCase)
                    && media.WinPE.ContainsKey(defaultLanguage))
                {
                    await BuildRunner.Run(recovery.SetAllInternationalAsync(defaultLanguage, progress, cancellationToken)).ConfigureAwait(false);
                }
            }

            if (plan.SafeOS is { } safeOS)
            {
                log($"  Safe OS Dynamic Update {Path.GetFileName(safeOS)}");
                await recovery.ApplyAsync(new AddPackageAction(safeOS, "Safe OS Dynamic Update"), progress, cancellationToken).ConfigureAwait(false);
                changed = true;
            }

            if (!changed)
            {
                await recovery.DiscardAsync(progress, cancellationToken).ConfigureAwait(false);
                return warnings;
            }

            if (cleanup)
            {
                await BuildRunner.Run(recovery.CleanupComponentsAsync(resetBase: true, progress, cancellationToken)).ConfigureAwait(false);
            }

            await recovery.CommitAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        // Re-export to shrink WinRE, then put it back where Windows expects it.
        var compacted = Path.Combine(work, "winre-final.wim");
        await BuildRunner.Run(images.ExportAsync(workWinRE, 1, compacted, progress: progress, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var serviced = WinREVersion(compacted);
        var before = new FileInfo(winRE).Length;
        var after = new FileInfo(compacted).Length;
        log($"WinRE: build {original.Build}.{original.Revision} -> {serviced.Build}.{serviced.Revision}, {Megabytes(before)} MB -> {Megabytes(after)} MB");
        if (plan.SafeOS is not null && serviced.Revision <= original.Revision)
        {
            warnings.Add($"WinRE is still at build {serviced.Build}.{serviced.Revision} after the Safe OS Dynamic Update; the update may be older than the WinRE already in the image.");
        }

        if (plan.Languages.Count > 0 && after > before)
        {
            // Deployed devices keep WinRE on the recovery partition; if it doesn't fit, Windows disables recovery.
            warnings.Add($"Localizing grew WinRE from {Megabytes(before)} MB to {Megabytes(after)} MB. Make sure the recovery partition your deployment creates has room for it.");
        }

        var attributes = File.GetAttributes(winRE);
        File.SetAttributes(winRE, FileAttributes.Normal);
        File.Copy(compacted, winRE, overwrite: true);
        File.SetAttributes(winRE, attributes);
        return warnings;
    }

    private Version WinREVersion(string winRE) =>
        images.GetIndex(winRE, 1)?.Version ?? throw new BuildException($"{winRE} has no image at index 1.");

    // 0x8007007E as dism.exe's exit code.
    private const int CombinedUpdateKnownIssue = unchecked((int)0x8007007E);

    /// <summary>The newest Safe OS Dynamic Update in the updates folder's SafeOS subfolder, by package version.</summary>
    public async Task<string?> FindSafeOSUpdateAsync(string updatesFolder, List<string> warnings, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(updatesFolder, UpdatesSection.SafeOSFolderName);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var candidates = new List<(string Path, Version? Version)>();
        foreach (var cab in Directory.EnumerateFiles(folder, "*.cab"))
        {
            var identity = await readCabIdentity(cab, cancellationToken).ConfigureAwait(false);
            candidates.Add((cab, identity?.Version));
        }

        if (candidates.Count > 1)
        {
            warnings.Add($"{folder} has {candidates.Count} Safe OS updates; using the newest. Keep only one there.");
        }

        return candidates.OrderByDescending(c => c.Version).Select(c => c.Path).FirstOrDefault();
    }

    private static long Megabytes(long bytes) => (bytes + (1 << 20) - 1) >> 20;
}
