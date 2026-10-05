using System.Globalization;
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
public sealed class BuildRunner(ReleaseCatalog? catalog = null, UpdateDownloader? downloader = null, DismCommand? dismCommand = null)
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
            await DownloadUpdatesAsync(download, info.ProductVersion.Build, info.InstallationType, architecture, progress, log, cancellationToken).ConfigureAwait(false);
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
        return new BuildResult(recipe.Output.Path, warnings);
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

            if (media is not null && recipe.Languages is { } languages)
            {
                if (mediaBuild is not null && baseline is not null && mediaBuild != baseline)
                {
                    throw new BuildException(
                        $"The language media is for build {mediaBuild}, but this image is serviced as build {baseline}. Use the Languages and Optional Features media for {baseline}.");
                }

                // Languages go in before cumulative updates, so the update also refreshes the new language resources.
                if (languages.UpdateWinRE)
                {
                    warnings.AddRange(await LocalizeWinREAsync(image, languages, media, work, recipe.Output.CleanupComponents, progress, log, cancellationToken).ConfigureAwait(false));
                }

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

            // After languages, so the cumulative update also services the newly added language resources.
            if (source.UpdateFiles is { } files)
            {
                var build = version?.CurrentBuild ?? baseline;
                var release = build is null ? null : catalog?.Resolve(build.Value, source.InstallationType, version?.DisplayVersion ?? version?.ReleaseId);
                var plan = UpdatePlanner.Plan(files, new UpdateTarget(release, source.Architecture, version?.UpdateBuildRevision), catalog);
                warnings.AddRange(plan.Warnings);

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
        UpdatesSection updates, int build, string installationType, string architecture,
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
            var selection = await downloader.SelectAsync(release, architecture, updates.IncludeDotNet, cancellationToken: cancellationToken).ConfigureAwait(false);
            log($"Downloading updates for {release.Name} {architecture}: {selection.CumulativeUpdate.Title}"
                + (selection.DotNet is { } dotNet ? $"; {dotNet.Title}" : string.Empty));

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

    private async Task<IReadOnlyList<string>> LocalizeWinREAsync(
        MountedImage image,
        LanguagesSection languages,
        LanguageMedia media,
        string work,
        bool cleanup,
        IProgress<ServicingProgress>? progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var winRE = Path.Combine(image.MountPath, "Windows", "System32", "Recovery", "Winre.wim");
        if (!File.Exists(winRE))
        {
            return ["The image has no WinRE (Windows\\System32\\Recovery\\Winre.wim); recovery was not localized."];
        }

        var workWinRE = Path.Combine(work, "winre.wim");
        File.Copy(winRE, workWinRE);
        File.SetAttributes(workWinRE, FileAttributes.Normal);

        IReadOnlyList<string> warnings;
        await using (var recovery = await MountedImage.MountAsync(workWinRE, 1, Path.Combine(work, "winre-mount"), progress, cancellationToken).ConfigureAwait(false))
        {
            var plan = LanguagePlanner.PlanWinPE(languages.Languages, media, recovery.GetPackages().Select(p => p.ToString()).ToList());
            warnings = plan.Warnings;
            if (plan.Actions.Count == 0)
            {
                await recovery.DiscardAsync(progress, cancellationToken).ConfigureAwait(false);
                return warnings;
            }

            log($"Localizing WinRE ({plan.Actions.Count} packages)");
            foreach (var action in plan.Actions)
            {
                log($"  {action.Description}");
                await recovery.ApplyAsync(action, progress, cancellationToken).ConfigureAwait(false);
            }

            if (languages.DefaultLanguage is { } defaultLanguage && media.WinPE.ContainsKey(defaultLanguage))
            {
                await Run(recovery.RunExternalAsync(mount => _dism.SetAllInternationalAsync(mount, defaultLanguage, progress, cancellationToken))).ConfigureAwait(false);
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

        var attributes = File.GetAttributes(winRE);
        File.SetAttributes(winRE, FileAttributes.Normal);
        File.Copy(compacted, winRE, overwrite: true);
        File.SetAttributes(winRE, attributes);
        return warnings;
    }

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
