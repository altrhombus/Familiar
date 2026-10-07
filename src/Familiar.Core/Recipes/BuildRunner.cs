using System.Globalization;
using Familiar.Core.ConfigMgr;
using Familiar.Core.InboxApps;
using Familiar.Core.Languages;
using Familiar.Core.Releases;
using Familiar.Core.Servicing;
using Familiar.Core.Updates;
using Microsoft.Dism;

namespace Familiar.Core.Recipes;

/// <summary>A build stopped for a reason the user can act on (as opposed to a bug).</summary>
public sealed class BuildException(string message) : Exception(message);

public sealed record BuildResult(string OutputPath, IReadOnlyList<string> Warnings);

/// <summary>
/// Runs a <see cref="BuildRecipe"/>. The source image is never modified: the chosen index is exported to a
/// working copy, serviced there, and exported again to the output path. Requires an initialized DISM API.
/// </summary>
/// <param name="catalog">Release data, used to order updates and spot missing ones. Builds still run without it.</param>
/// <param name="downloader">Needed only for recipes that download updates.</param>
/// <param name="connectConfigMgr">Replaces the Configuration Manager connection (tests); defaults to <see cref="ConfigMgrConnector"/>.</param>
public sealed class BuildRunner(
    ReleaseCatalog? catalog = null,
    UpdateDownloader? downloader = null,
    DismCommand? dismCommand = null,
    Func<ConfigMgrSection, Action<string>, CancellationToken, Task<IConfigMgrConnection>>? connectConfigMgr = null)
{
    private readonly DismCommand _dism = dismCommand ?? new DismCommand();

    public async Task<BuildResult> RunAsync(
        BuildRecipe recipe,
        IProgress<ServicingProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        log ??= _ => { };
        var warnings = new List<string>();

        var problems = RecipeValidator.Validate(recipe);
        if (problems.Count > 0)
        {
            throw new BuildException(string.Join(Environment.NewLine, problems));
        }

        if (File.Exists(recipe.Output.Path))
        {
            throw new BuildException($"Output {recipe.Output.Path} already exists. Move it or choose another output.path.");
        }

        var media = recipe.Languages is null ? null : LanguageMedia.Scan(recipe.Languages.MediaPath);
        var mediaBuild = media is null ? null : await ReadMediaBuildAsync(media, cancellationToken).ConfigureAwait(false);

        var info = DismApi.GetImageInfo(recipe.Source.Path).FirstOrDefault(i => i.ImageIndex == recipe.Source.Index)
            ?? throw new BuildException($"Index {recipe.Source.Index} does not exist in {recipe.Source.Path}.");
        var architecture = info.Architecture == DismProcessorArchitecture.ARM64 ? "arm64" : "x64";

        // Download before mounting, so a long download never holds a mount open.
        if (recipe.Updates is { Download: true } download)
        {
            await DownloadUpdatesAsync(download, recipe.WinRE.Update, info.ProductVersion.Build, info.InstallationType, architecture, progress, log, cancellationToken).ConfigureAwait(false);
        }

        var updateFiles = recipe.Updates is null ? null : UpdatePlanner.Scan(recipe.Updates.Folder);
        if (updateFiles is { Count: 0 })
        {
            throw new BuildException($"No .msu or .cab files with a KB number were found in {recipe.Updates!.Folder}.");
        }

        var source = new SourceDetails(info.ImageName, info.InstallationType, architecture, updateFiles);

        var work = Path.Combine(recipe.WorkingDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(work);
        log($"Working folder: {work}");

        var started = DateTime.Now;
        try
        {
            await BuildAsync(recipe, media, mediaBuild, source, work, warnings, progress, log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Diagnostics.AsrBlocks.Find(started, work) is { Count: > 0 } blocks)
        {
            // DISM reports these as generic failures (often 0x800f0922); name the real cause.
            var details = string.Join(Environment.NewLine, blocks.Take(5).Select(b =>
                $"  {b.Time:HH:mm:ss} {b.RuleName}: {Path.GetFileName(b.Path)} (by {Path.GetFileName(b.ProcessName)})"));
            throw new BuildException(
                $"{Images.DismErrors.Describe(ex) ?? ex.Message}{Environment.NewLine}"
                + $"Microsoft Defender attack surface reduction blocked {blocks.Count} file operation(s) during the build, which is the likely cause:{Environment.NewLine}"
                + details + Environment.NewLine
                + "Ask whoever manages Defender policy about an exclusion for the working directory, or build on a machine where these rules are in audit mode.");
        }
        finally
        {
            // Mounted images are already unmounted (and discarded on failure) by the time we get here.
            TryDelete(work, log);
        }

        log($"Done: {recipe.Output.Path}");

        if (recipe.ConfigMgr is { } configMgr)
        {
            // Facts come from the built output, so templates and the downgrade check describe what is actually published.
            var facts = ImageFactsReader.Read(recipe.Output.Path, catalog);
            log($"Built image: {facts.Release ?? "unknown release"} {facts.Edition} {facts.Architecture}, build {facts.Build}{(facts.Kb is null ? string.Empty : $" ({facts.Kb})")}");
            warnings.AddRange(await PublishAsync(recipe, configMgr, recipe.Output.Path, facts, connectConfigMgr, progress, log, cancellationToken).ConfigureAwait(false));
        }

        return new BuildResult(recipe.Output.Path, warnings);
    }

    /// <summary>Publishes a built WIM to Configuration Manager as described by the recipe.</summary>
    public static async Task<IReadOnlyList<string>> PublishAsync(
        BuildRecipe recipe,
        ConfigMgrSection section,
        string wimPath,
        ImageFacts? facts,
        Func<ConfigMgrSection, Action<string>, CancellationToken, Task<IConfigMgrConnection>>? connect,
        IProgress<ServicingProgress>? progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(section);
        connect ??= (s, l, ct) => ConfigMgrConnector.ConnectAsync(s.SiteServer, s.SiteCode, s.Transport, l, ct);

        try
        {
            var now = DateTime.Now;
            string? Expand(string? template) => template is null ? null : PackageTemplate.Expand(template, facts, now);

            // New packages need a name and get a dated version by default; existing ones change only what the recipe sets.
            var isNew = section.PackageId is null;
            var request = new ConfigMgrPublishRequest
            {
                PackageId = section.PackageId,
                PackageSourcePath = section.PackageSourcePath,
                Name = Expand(section.PackageName) ?? (isNew ? recipe.Name ?? Path.GetFileNameWithoutExtension(wimPath) : null),
                Version = Expand(section.Version) ?? (isNew ? now.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) : null),
                Description = Expand(section.Description),
                DistributionPointGroups = section.DistributionPointGroups,
                DistributionPoints = section.DistributionPoints,
                RefreshDistributionPoints = section.RefreshDistributionPoints,
                SourceFolderName = section.SourceFolderName,
                Image = facts,
                AllowOlderImage = section.AllowOlderImage,
            };

            if (request.Name is not null || request.Version is not null || request.Description is not null)
            {
                log($"Package fields: name \"{request.Name ?? "(unchanged)"}\", version \"{request.Version ?? "(unchanged)"}\", description \"{request.Description ?? "(unchanged)"}\"");
            }

            await using var connection = await connect(section, log, cancellationToken).ConfigureAwait(false);

            var result = await new ConfigMgrPublisher(connection).PublishAsync(wimPath, request, progress, log, cancellationToken).ConfigureAwait(false);
            return result.Warnings;
        }
        catch (Exception ex) when (ex is ConfigMgrException or IOException or UnauthorizedAccessException)
        {
            // The image itself is built; say so, so nobody rebuilds just to retry publishing.
            throw new BuildException($"The image was built ({wimPath}) but publishing to Configuration Manager failed: {ex.Message}"
                + $"{Environment.NewLine}Retry with 'familiar configmgr publish <recipe>'.");
        }
    }

    private async Task BuildAsync(
        BuildRecipe recipe,
        LanguageMedia? media,
        int? mediaBuild,
        SourceDetails source,
        string work,
        List<string> warnings,
        IProgress<ServicingProgress>? progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var workImage = Path.Combine(work, "install.wim");
        await Run(_dism.ExportImageAsync(recipe.Source.Path, recipe.Source.Index, workImage, progress: progress, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await using (var image = await MountedImage.MountAsync(workImage, 1, Path.Combine(work, "mount"), progress, cancellationToken).ConfigureAwait(false))
        {
            var baseline = image.GetServicingBaselineBuild();

            // The baseline check relies on the Foundation package; the registry adds the display version and revision.
            OfflineWindowsVersion? version = null;
            try
            {
                version = image.ReadWindowsVersion();
                log($"Image: {source.Name}, build {version.CurrentBuild}.{version.UpdateBuildRevision} ({version.DisplayVersion ?? version.ReleaseId}), servicing baseline {baseline}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                warnings.Add($"Could not read the image's registry version: {ex.Message}");
                log($"Image: {source.Name}, servicing baseline {baseline}");
            }

            if (media is not null && mediaBuild is not null && baseline is not null && mediaBuild != baseline)
            {
                throw new BuildException(
                    $"The language media is for build {mediaBuild}, but this image is serviced as build {baseline}. Use the Languages and Optional Features media for {baseline}.");
            }

            var build = version?.CurrentBuild ?? baseline;
            var release = build is null ? null : catalog?.Resolve(build.Value, source.InstallationType, version?.DisplayVersion ?? version?.ReleaseId);

            var safeOS = recipe.WinRE.Update && recipe.Updates is { } updatesSection
                ? await FindSafeOSUpdateAsync(updatesSection.Folder, warnings, cancellationToken).ConfigureAwait(false)
                : null;
            if (recipe.WinRE.Update && recipe.Updates is not null && safeOS is null)
            {
                warnings.Add($"No Safe OS Dynamic Update in {Path.Combine(recipe.Updates.Folder, UpdatesSection.SafeOSFolderName)}, so WinRE was not patched. Set updates.download, or put the .cab there.");
            }

            var winREWork = new WinREWork(recipe.WinRE.ResolveLanguages(recipe.Languages), media, recipe.Languages?.DefaultLanguage, source.UpdateFiles, safeOS);
            if (winREWork.HasWork)
            {
                warnings.AddRange(await ServiceWinREAsync(image, winREWork, release, source.Architecture, work, recipe.Output.CleanupComponents, progress, log, cancellationToken).ConfigureAwait(false));
            }

            if (media is not null && recipe.Languages is { } languages)
            {
                // Languages go in before cumulative updates, so the update also refreshes the new language resources.
                var request = new LanguageRequest
                {
                    Languages = languages.Languages,
                    Features = languages.Features is { Count: > 0 } features
                        ? new HashSet<LanguageFeature>(features)
                        : new HashSet<LanguageFeature>(Enum.GetValues<LanguageFeature>()),
                    IncludeFonts = languages.IncludeFonts,
                };

                var plan = LanguagePlanner.PlanImage(request, media, image.GetLanguageState());
                warnings.AddRange(plan.Warnings);
                if (!plan.IsValid)
                {
                    throw new BuildException(string.Join(Environment.NewLine, plan.Errors));
                }

                log($"Adding {plan.Actions.Count} language packages and features");
                foreach (var action in plan.Actions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log($"  {action.Description}");
                    await image.ApplyAsync(action, progress, cancellationToken).ConfigureAwait(false);
                }

                if (languages.DefaultLanguage is { } defaultLanguage)
                {
                    log($"Setting default language to {defaultLanguage}");
                    await Run(image.RunExternalAsync(mount => _dism.SetAllInternationalAsync(mount, defaultLanguage, progress, cancellationToken))).ConfigureAwait(false);
                }
            }

            // After languages, so bundles provision resource packages for the new languages.
            if (recipe.InboxApps is { } inboxApps)
            {
                var plan = InboxAppPlanner.Plan(InboxAppMedia.Scan(inboxApps.MediaPath), image.GetProvisionedApps(), source.Architecture);
                warnings.AddRange(plan.Warnings);

                log($"Re-provisioning {plan.Actions.Count} inbox apps");
                foreach (var action in plan.Actions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log($"  {action.Description}");
                    await image.ApplyAsync(action, progress, cancellationToken).ConfigureAwait(false);
                }
            }

            // After languages, so the cumulative update also services the newly added language resources.
            if (source.UpdateFiles is { } files)
            {
                var plan = UpdatePlanner.Plan(files, new UpdateTarget(release, source.Architecture, version?.UpdateBuildRevision), catalog);
                warnings.AddRange(plan.Warnings);
                foreach (var note in plan.Notes)
                {
                    log(note);
                }

                log($"Adding {plan.Actions.Count} updates");
                var scratch = Path.Combine(work, "scratch");
                foreach (var action in plan.Actions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log($"  {action.Description}");

                    if (action is AddPackageAction { PackagePath: var package } && package.EndsWith(".msu", StringComparison.OrdinalIgnoreCase))
                    {
                        await Run(image.RunExternalAsync(mount =>
                            _dism.AddPackageAsync(mount, package, scratch, action.Description, progress, cancellationToken))).ConfigureAwait(false);
                    }
                    else
                    {
                        await image.ApplyAsync(action, progress, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (recipe.Output.CleanupComponents)
            {
                log("Cleaning up components");
                await Run(image.RunExternalAsync(mount => _dism.CleanupComponentsAsync(mount, resetBase: false, progress, cancellationToken))).ConfigureAwait(false);
            }

            log("Saving image");
            await image.CommitAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        // Exporting drops the space left behind by replaced files and stores the final name.
        Directory.CreateDirectory(Path.GetDirectoryName(recipe.Output.Path)!);
        await Run(_dism.ExportImageAsync(workImage, 1, recipe.Output.Path, recipe.Output.ImageName ?? source.Name, progress, cancellationToken)).ConfigureAwait(false);
    }

    private async Task DownloadUpdatesAsync(
        UpdatesSection updates, bool includeSafeOS, int build, string installationType, string architecture,
        IProgress<ServicingProgress>? progress, Action<string> log, CancellationToken cancellationToken)
    {
        if (catalog is null || downloader is null)
        {
            throw new BuildException("updates.download needs release data and network access; run without --offline.");
        }

        var release = catalog.Resolve(build, installationType)
            ?? throw new BuildException($"Build {build} is not in the release data, so Familiar cannot tell which updates to download. Try --refresh.");

        try
        {
            var selection = await downloader.SelectAsync(release, architecture, updates.IncludeDotNet, includeSafeOS: includeSafeOS, cancellationToken: cancellationToken).ConfigureAwait(false);
            log($"Downloading updates for {release.Name} {architecture}: {selection.CumulativeUpdate.Title}"
                + (selection.DotNet is { } dotNet ? $"; {dotNet.Title}" : string.Empty)
                + (selection.SafeOS is { } safeOS ? $"; {safeOS.Title}" : string.Empty));

            var result = await downloader.DownloadAsync(selection, updates.Folder, progress, cancellationToken).ConfigureAwait(false);
            foreach (var file in result.Files)
            {
                log($"  {Path.GetFileName(file.Path)}{(file.AlreadyPresent ? " (already downloaded)" : string.Empty)}");
            }

            foreach (var removed in result.Removed)
            {
                log($"  Removed superseded {Path.GetFileName(removed)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or NotSupportedException)
        {
            throw new BuildException($"Downloading updates failed: {ex.Message}");
        }
    }

    private sealed record SourceDetails(string Name, string InstallationType, string Architecture, IReadOnlyList<UpdateFile>? UpdateFiles);

    private static void TryDelete(string folder, Action<string> log)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Could not remove working folder {folder}: {ex.Message}");
        }
    }

    /// <summary>
    /// Patches and/or localizes WinRE in Microsoft's documented order: the cumulative update (for its servicing
    /// stack), languages, the Safe OS Dynamic Update, cleanup, export.
    /// </summary>
    private async Task<IReadOnlyList<string>> ServiceWinREAsync(
        MountedImage image,
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

        var original = DismApi.GetImageInfo(workWinRE)[0].ProductVersion;
        log($"WinRE: build {original.Build}.{original.Revision}");

        var warnings = new List<string>();
        var changed = false;
        await using (var recovery = await MountedImage.MountAsync(workWinRE, 1, Path.Combine(work, "winre-mount"), progress, cancellationToken).ConfigureAwait(false))
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
                    var result = await recovery.RunExternalAsync(mount =>
                        _dism.AddPackageAsync(mount, action.PackagePath, scratch, $"WinRE: {action.Description}", progress, cancellationToken)).ConfigureAwait(false);
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
                    await Run(recovery.RunExternalAsync(mount => _dism.SetAllInternationalAsync(mount, defaultLanguage, progress, cancellationToken))).ConfigureAwait(false);
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
                await Run(recovery.RunExternalAsync(mount => _dism.CleanupComponentsAsync(mount, resetBase: true, progress, cancellationToken))).ConfigureAwait(false);
            }

            await recovery.CommitAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        // Re-export to shrink WinRE, then put it back where Windows expects it.
        var compacted = Path.Combine(work, "winre-final.wim");
        await Run(_dism.ExportImageAsync(workWinRE, 1, compacted, progress: progress, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var serviced = DismApi.GetImageInfo(compacted)[0].ProductVersion;
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

    // 0x8007007E as dism.exe's exit code.
    private const int CombinedUpdateKnownIssue = unchecked((int)0x8007007E);

    /// <summary>What a build does to WinRE.</summary>
    /// <param name="Updates">Cumulative updates for WinRE's servicing stack; applied only along with <paramref name="SafeOS"/>.</param>
    /// <param name="SafeOS">The Safe OS Dynamic Update .cab, or null to leave WinRE unpatched.</param>
    private sealed record WinREWork(
        IReadOnlyList<string> Languages,
        LanguageMedia? Media,
        string? DefaultLanguage,
        IReadOnlyList<UpdateFile>? Updates,
        string? SafeOS)
    {
        public bool HasWork => SafeOS is not null || (Languages.Count > 0 && Media is not null);
    }

    /// <summary>The newest Safe OS Dynamic Update in the updates folder's SafeOS subfolder, by package version.</summary>
    private static async Task<string?> FindSafeOSUpdateAsync(string updatesFolder, List<string> warnings, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(updatesFolder, UpdatesSection.SafeOSFolderName);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var candidates = new List<(string Path, Version? Version)>();
        foreach (var cab in Directory.EnumerateFiles(folder, "*.cab"))
        {
            var identity = await CabManifest.ReadIdentityAsync(cab, cancellationToken).ConfigureAwait(false);
            candidates.Add((cab, identity?.Version));
        }

        if (candidates.Count > 1)
        {
            warnings.Add($"{folder} has {candidates.Count} Safe OS updates; using the newest. Keep only one there.");
        }

        return candidates.OrderByDescending(c => c.Version).Select(c => c.Path).FirstOrDefault();
    }

    private static long Megabytes(long bytes) => (bytes + (1 << 20) - 1) >> 20;

    // Any language pack on the media carries the build it was made for.
    private static async Task<int?> ReadMediaBuildAsync(LanguageMedia media, CancellationToken cancellationToken)
    {
        var sample = media.LanguagePacks.Values.FirstOrDefault(p => !p.IsInterfacePack);
        if (sample is null)
        {
            return null;
        }

        var identity = await CabManifest.ReadIdentityAsync(sample.Path, cancellationToken).ConfigureAwait(false);
        return identity?.Version?.Build;
    }

    private static async Task Run(Task<DismCommandResult> command)
    {
        var result = await command.ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new BuildException($"dism.exe failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output.Trim()}");
        }
    }
}
