using Familiar.Core.Servicing;

namespace Familiar.Core.ConfigMgr;

/// <summary>What to publish and where. Mirrors the recipe's <c>configMgr</c> section.</summary>
public sealed record ConfigMgrPublishRequest
{
    /// <summary>Update this existing OS image package. When null, a new package is created.</summary>
    public string? PackageId { get; init; }

    /// <summary>UNC folder for the package source. Required for new packages; for existing ones, overrides nothing.</summary>
    public string? PackageSourcePath { get; init; }

    /// <summary>Package name: required for new packages; for existing ones, renames when set.</summary>
    public string? Name { get; init; }

    /// <summary>Package version; for existing packages, changed only when set.</summary>
    public string? Version { get; init; }

    /// <summary>Package description (the console's comment); for existing packages, changed only when set.</summary>
    public string? Description { get; init; }

    public IReadOnlyList<string> DistributionPointGroups { get; init; } = [];

    public IReadOnlyList<string> DistributionPoints { get; init; } = [];

    /// <summary>
    /// For existing packages: push the new source to distribution points right away. When false the source and
    /// image properties are updated but distribution points keep serving the previous content until someone
    /// updates them (console: Update Distribution Points), which leaves room to test first.
    /// </summary>
    public bool RefreshDistributionPoints { get; init; } = true;

    /// <summary>
    /// Folder-name template (see <see cref="SourceFolderTemplate"/>). When set, every publish goes into a new folder
    /// under <see cref="PackageSourcePath"/> (for updates, beside the current folder unless that is set) and existing
    /// packages are repointed; when null, new packages go straight into PackageSourcePath and updates overwrite.
    /// </summary>
    public string? SourceFolderName { get; init; }

    /// <summary>Facts about the image being published, for templates and the downgrade check.</summary>
    public ImageFacts? Image { get; init; }

    /// <summary>
    /// Publish to an existing package even when the image is an older build than the package already has. Off by
    /// default: a skipped or failed update, or older media, would otherwise roll deployments back.
    /// </summary>
    public bool AllowOlderImage { get; init; }

    /// <summary>
    /// How many replaced images to keep for rollback: in place, <c>.previous</c> and the numbered ones before it;
    /// with versioned folders, earlier folders Familiar published for this package. Older ones are deleted after a
    /// successful publish, except folders an OS image package still uses. 0 keeps none, so nothing can be rolled back.
    /// </summary>
    public int KeepPreviousImages { get; init; } = 1;

    /// <summary>
    /// For a new package, replace a file already at the target path instead of refusing. Still refused when an OS
    /// image package uses that file; other package types are not checked.
    /// </summary>
    public bool OverwriteExistingSource { get; init; }
}

/// <summary>Written into each versioned folder so rollback knows which source it replaced.</summary>
internal sealed record PublishRecord(string PackageId, string PreviousSourcePath, DateTimeOffset PublishedAt)
{
    public const string FileName = "familiar-publish.json";

    public static void Write(string folder, PublishRecord record) =>
        File.WriteAllText(Path.Combine(folder, FileName), System.Text.Json.JsonSerializer.Serialize(record, PublishRecordJsonContext.Default.PublishRecord));

    public static PublishRecord? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), PublishRecordJsonContext.Default.PublishRecord) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(PublishRecord))]
internal sealed partial class PublishRecordJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <param name="PreviousImagePath">Where the replaced WIM was kept, when an existing package was updated.</param>
public sealed record ConfigMgrPublishResult(string PackageId, bool Created, string? PreviousImagePath, IReadOnlyList<string> Warnings);

/// <summary>
/// Puts a built WIM into Configuration Manager: copy to the package source, create or update the OS image
/// package, reload its image properties, then distribute (new packages) or refresh distribution points
/// (existing packages).
/// </summary>
/// <param name="time">Clock for folder names and publish records; the system clock unless a test sets one.</param>
public sealed class ConfigMgrPublisher(IConfigMgrConnection connection, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<ConfigMgrPublishResult> PublishAsync(
        string wimPath,
        ConfigMgrPublishRequest request,
        IProgress<ServicingProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        log ??= _ => { };
        var warnings = new List<string>();

        if (!File.Exists(wimPath))
        {
            throw new ConfigMgrException($"Nothing to publish: {wimPath} does not exist.");
        }

        // Resolve distribution targets first, so a typo fails before anything is copied or created.
        var groups = await ResolveGroupsAsync(request.DistributionPointGroups, cancellationToken).ConfigureAwait(false);
        var points = await ResolvePointsAsync(request.DistributionPoints, cancellationToken).ConfigureAwait(false);

        string packageId;
        bool created;
        string? previous = null;
        long? sourceVersionBefore = null;
        if (request.PackageId is { } existingId)
        {
            var package = await connection.GetImagePackageAsync(existingId, cancellationToken).ConfigureAwait(false)
                ?? throw new ConfigMgrException($"OS image package {existingId} was not found on site {connection.SiteCode}.");
            if (string.IsNullOrWhiteSpace(package.SourcePath))
            {
                throw new ConfigMgrException($"OS image package {existingId} has no source path.");
            }

            log($"Updating OS image package {package.PackageId} ({package.Name}); source {package.SourcePath}");
            sourceVersionBefore = package.SourceVersion;

            // Checked before anything is copied: replacing an image with an older build silently un-patches deployments.
            if (IsOlder(request.Image?.Build, package.ImageOSVersion) is { } comparison && !request.AllowOlderImage)
            {
                throw new ConfigMgrException(
                    $"The new image is build {comparison.New}, older than the {comparison.Current} in {package.PackageId}. "
                    + "Check that updates were applied, or set configMgr.allowOlderImage to publish it anyway.");
            }

            if (request.SourceFolderName is { } template)
            {
                // Versioned folders: copy beside the current one, then repoint the package. The old folder is the rollback.
                var parent = request.PackageSourcePath ?? Path.GetDirectoryName(Path.GetDirectoryName(package.SourcePath))!;
                var folder = SourceFolderTemplate.Expand(template, parent, request.Image, _time.GetLocalNow().DateTime);
                var target = Path.Combine(folder, Path.GetFileName(package.SourcePath));

                log($"Copying image to {target}");
                await CopyAsync(wimPath, target, overwrite: false, progress, cancellationToken).ConfigureAwait(false);
                PublishRecord.Write(folder, new PublishRecord(package.PackageId, package.SourcePath, _time.GetLocalNow()));

                log($"Pointing {package.PackageId} at {target}");
                try
                {
                    await connection.UpdateImagePackageAsync(package.PackageId, request.Version, request.Description, target, request.Name, cancellationToken).ConfigureAwait(false);
                }
                catch (ConfigMgrException ex)
                {
                    throw new ConfigMgrException($"The image was copied to {target}, but {package.PackageId} still points at {package.SourcePath}: {ex.Message}", ex);
                }

                // A site can accept an update without applying it; never report a repoint that did not happen.
                await VerifySourceAsync(package.PackageId, target, cancellationToken).ConfigureAwait(false);

                previous = package.SourcePath;
                await TrimPublishedFoldersAsync(package.PackageId, parent, folder, Path.GetDirectoryName(previous)!, request.KeepPreviousImages, log, warnings, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // In place: task sequences deploy whatever the package source holds, so keep the replaced image for rollback.
                if (File.Exists(package.SourcePath))
                {
                    previous = await ReplaceKeepingPreviousAsync(wimPath, package.SourcePath, progress, log, cancellationToken).ConfigureAwait(false);
                    if (TrimKeptImages(package.SourcePath, request.KeepPreviousImages, log, warnings) == 0)
                    {
                        previous = null;
                    }
                }
                else
                {
                    await CopyAsync(wimPath, package.SourcePath, overwrite: false, progress, cancellationToken).ConfigureAwait(false);
                }

                if (request.Version is not null || request.Description is not null || request.Name is not null)
                {
                    try
                    {
                        await connection.UpdateImagePackageAsync(package.PackageId, request.Version, request.Description, name: request.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (ConfigMgrException ex)
                    {
                        warnings.Add($"Version and description were not updated: {ex.Message}");
                    }
                }
            }

            packageId = package.PackageId;
            created = false;
        }
        else
        {
            var root = request.PackageSourcePath
                ?? throw new ConfigMgrException("configMgr.packageSourcePath is required to create a new OS image package.");
            var folder = request.SourceFolderName is { } template
                ? SourceFolderTemplate.Expand(template, root, request.Image, _time.GetLocalNow().DateTime)
                : root;
            var target = Path.Combine(folder, Path.GetFileName(wimPath));

            // Another package (or an earlier publish) may deploy this file; a new package must not replace it unasked.
            var replacing = File.Exists(target);
            if (replacing && !request.OverwriteExistingSource)
            {
                throw new ConfigMgrException(
                    $"{target} already exists, and another package may deploy it. Set configMgr.sourceFolderName (for example \"{{release}}_{{arch}}_{{n}}\") "
                    + "so each publish gets its own folder, choose another packageSourcePath, or set packageId to update the package that uses it. "
                    + "If it is left from an earlier publish that failed, publish again with --force to replace it.");
            }

            if (replacing)
            {
                var user = (await connection.GetImagePackagesAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(p => p.SourcePath is { } s && SamePath(s, target));
                if (user is not null)
                {
                    throw new ConfigMgrException(
                        $"{target} is the source of OS image package {user.PackageId} ({user.Name}), so it was not replaced. Set packageId to {user.PackageId} to update that package.");
                }

                log($"Replacing {target}, which no OS image package uses (--force)");
            }

            log($"Copying image to {target}");
            await CopyAsync(wimPath, target, overwrite: replacing, progress, cancellationToken).ConfigureAwait(false);

            var name = request.Name ?? throw new ConfigMgrException("A name is required to create a new OS image package.");
            log($"Creating OS image package \"{name}\"");
            packageId = await connection.CreateImagePackageAsync(name, request.Version, request.Description, target, cancellationToken).ConfigureAwait(false);
            created = true;
        }

        log($"Reloading image properties for {packageId}");
        await connection.ReloadImagePropertiesAsync(packageId, cancellationToken).ConfigureAwait(false);

        if (!created && request.RefreshDistributionPoints)
        {
            log("Refreshing distribution points that already have the package");
            await connection.RefreshDistributionPointsAsync(packageId, cancellationToken).ConfigureAwait(false);
        }
        else if (!created)
        {
            // Holding content back assumes the site only redistributes when asked; check that it did not start anyway.
            var after = (await connection.GetImagePackageAsync(packageId, cancellationToken).ConfigureAwait(false))?.SourceVersion;
            if (sourceVersionBefore is not null && after is not null && after != sourceVersionBefore)
            {
                warnings.Add($"refreshDistributionPoints is false, but {packageId}'s content version changed from {sourceVersionBefore} to {after}: the site may already be sending the new image to distribution points. Check its content status in the console.");
            }
            else
            {
                warnings.Add($"Distribution points still serve the previous content of {packageId} (content version {after?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}). Update them from the console when the new image is tested, or run 'familiar configmgr rollback' to restore the previous image.");
            }
        }

        var targeted = created ? [] : await connection.GetPackageTargetsAsync(packageId, cancellationToken).ConfigureAwait(false);

        foreach (var group in groups)
        {
            log($"Distributing to group {group.Name}");
            try
            {
                await connection.DistributeToGroupAsync(packageId, group.GroupId, cancellationToken).ConfigureAwait(false);
            }
            catch (ConfigMgrException ex) when (!created)
            {
                // Usually means the package is already in the group; the refresh above covers it.
                warnings.Add($"Group {group.Name}: {ex.Message}");
            }
        }

        foreach (var point in points.Where(p => !targeted.Contains(p.NalPath, StringComparer.OrdinalIgnoreCase)))
        {
            log($"Distributing to {point.ServerName}");
            await connection.DistributeToPointAsync(packageId, point, cancellationToken).ConfigureAwait(false);
        }

        if (created && groups.Count == 0 && points.Count == 0)
        {
            warnings.Add($"{packageId} was created but not distributed; add distributionPointGroups or distributionPoints, or distribute it in the console.");
        }

        log($"Published {packageId} on site {connection.SiteCode} via {connection.Transport}");
        return new ConfigMgrPublishResult(packageId, created, previous, warnings);
    }

    /// <summary>
    /// Undoes the last update of <paramref name="packageId"/>, then reloads image properties and refreshes
    /// distribution points. Versioned-folder updates point the package back at the folder they replaced (the new
    /// folder is left in place); in-place updates restore the <c>.previous</c> image and keep the rolled-back one
    /// as <c>.rejected</c>.
    /// </summary>
    public async Task RollbackAsync(string packageId, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        log ??= _ => { };
        var package = await connection.GetImagePackageAsync(packageId, cancellationToken).ConfigureAwait(false)
            ?? throw new ConfigMgrException($"OS image package {packageId} was not found on site {connection.SiteCode}.");
        var source = package.SourcePath ?? throw new ConfigMgrException($"OS image package {packageId} has no source path.");

        // An image kept beside the source is always the most recent replacement: a package published into versioned
        // folders and later in place has both, and the in-place one came last.
        var previous = PreviousImagePath(source);
        if (File.Exists(previous))
        {
            log($"Restoring {previous} to {source}");
            string? rejected = null;
            if (File.Exists(source))
            {
                rejected = source + ".rejected";
                File.Move(source, rejected, overwrite: true);
            }

            try
            {
                File.Move(previous, source);
            }
            catch
            {
                // Never leave the package without a source: put the image back as it was.
                if (rejected is not null)
                {
                    File.Move(rejected, source);
                }

                throw;
            }

            // The next rollback goes one image further back.
            if (EarlierImagePaths(source) is { Count: > 0 } older)
            {
                File.Move(older[^1], previous);
                log($"Kept {older[^1]} as {previous}; the next rollback restores it");
            }
        }
        else if (PublishRecord.Read(Path.GetDirectoryName(source)!) is { PackageId: var recorded, PreviousSourcePath: var earlier }
            && string.Equals(recorded, package.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(earlier))
            {
                throw new ConfigMgrException($"The image this update replaced is gone ({earlier} does not exist).");
            }

            log($"Pointing {package.PackageId} back at {earlier}");
            await connection.UpdateImagePackageAsync(package.PackageId, version: null, description: null, earlier, cancellationToken: cancellationToken).ConfigureAwait(false);
            await VerifySourceAsync(package.PackageId, earlier, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ConfigMgrException($"No previous image to roll back to: {previous} does not exist, and {source} was not published by Familiar into a versioned folder.");
        }

        log($"Reloading image properties for {package.PackageId}");
        await connection.ReloadImagePropertiesAsync(package.PackageId, cancellationToken).ConfigureAwait(false);
        log("Refreshing distribution points");
        await connection.RefreshDistributionPointsAsync(package.PackageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compares "26200.9457" with an ImageOSVersion like "10.0.26200.7628"; null unless the new one is older.</summary>
    internal static (string New, string Current)? IsOlder(string? newBuild, string? currentImageVersion)
    {
        if (newBuild is null || currentImageVersion is null
            || !Version.TryParse("10.0." + newBuild, out var candidate) || !Version.TryParse(currentImageVersion, out var current))
        {
            return null;
        }

        return candidate < current ? (newBuild, $"{current.Build}.{current.Revision}") : null;
    }

    private async Task VerifySourceAsync(string packageId, string expected, CancellationToken cancellationToken)
    {
        var actual = (await connection.GetImagePackageAsync(packageId, cancellationToken).ConfigureAwait(false))?.SourcePath;
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigMgrException(
                $"The site accepted the change, but {packageId} still points at {actual ?? "nothing"} instead of {expected}. Nothing else was changed.");
        }
    }

    /// <summary>Where the replaced image is kept: beside the source, under a name the package does not reference.</summary>
    public static string PreviousImagePath(string sourcePath) => sourcePath + ".previous";

    /// <summary>
    /// Images replaced before the one at <see cref="PreviousImagePath"/>, oldest first: <c>install.wim.previous.1</c>,
    /// <c>.2</c> and so on, numbered in the order they were kept.
    /// </summary>
    internal static IReadOnlyList<string> EarlierImagePaths(string sourcePath)
    {
        var folder = Path.GetDirectoryName(sourcePath)!;
        if (!Directory.Exists(folder))
        {
            return [];
        }

        // Windows also matches "install.wim.previous" itself against "install.wim.previous.*", so check the prefix too.
        var prefix = Path.GetFileName(PreviousImagePath(sourcePath)) + ".";
        return Directory.EnumerateFiles(folder, prefix + "*")
            .Where(path => Path.GetFileName(path).Length > prefix.Length && Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Number: int.TryParse(Path.GetFileName(path).AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0))
            .Where(e => e.Number > 0)
            .OrderBy(e => e.Number)
            .Select(e => e.Path)
            .ToList();
    }

    /// <summary>
    /// Deletes kept images beyond the newest <paramref name="keep"/> (<c>.previous</c> first, then the numbered ones,
    /// newest first) and returns how many are left.
    /// </summary>
    private static int TrimKeptImages(string source, int keep, Action<string> log, List<string> warnings)
    {
        var kept = new[] { PreviousImagePath(source) }.Concat(EarlierImagePaths(source).Reverse()).Where(File.Exists).ToList();
        var left = kept.Count;
        foreach (var path in kept.Skip(keep))
        {
            try
            {
                File.Delete(path);
                left--;
                log($"Removed {path} (configMgr.keepPreviousImages is {keep.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not remove the older kept image {path}: {ex.Message}");
            }
        }

        return left;
    }

    /// <summary>
    /// Deletes folders Familiar published for <paramref name="packageId"/> under <paramref name="parent"/> beyond the
    /// newest <paramref name="keep"/>. The folder the package used until now counts first, so after a rollback the
    /// image that was rolled back to is kept rather than the rejected one. Folders without Familiar's publish record,
    /// and folders any OS image package still uses, are never deleted.
    /// </summary>
    private async Task TrimPublishedFoldersAsync(
        string packageId, string parent, string current, string replaced, int keep, Action<string> log, List<string> warnings, CancellationToken cancellationToken)
    {
        var surplus = Directory.EnumerateDirectories(parent)
            .Where(folder => !SamePath(folder, current))
            .Select(folder => (Folder: folder, Record: PublishRecord.Read(folder)))
            .Where(e => e.Record is { } record && string.Equals(record.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => SamePath(e.Folder, replaced))
            .ThenByDescending(e => e.Record!.PublishedAt)
            .Skip(keep)
            .Select(e => e.Folder)
            .ToList();
        if (surplus.Count == 0)
        {
            return;
        }

        var packages = await connection.GetImagePackagesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var folder in surplus)
        {
            if (packages.FirstOrDefault(p => p.SourcePath is { } s && IsInside(s, folder)) is { } user)
            {
                log($"Keeping {folder}: OS image package {user.PackageId} ({user.Name}) uses it");
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
                log($"Removed {folder} (configMgr.keepPreviousImages is {keep.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not remove the older published folder {folder}: {ex.Message}");
            }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the image at <paramref name="source"/>, keeping it as <see cref="PreviousImagePath"/> and any earlier
    /// previous image as the next of <see cref="EarlierImagePaths"/>. The new image is copied in full beside the source
    /// before anything is renamed, so a failed or cancelled copy leaves the source and every kept image as they were.
    /// </summary>
    private static async Task<string> ReplaceKeepingPreviousAsync(
        string wimPath, string source, IProgress<ServicingProgress>? progress, Action<string> log, CancellationToken cancellationToken)
    {
        var staged = await CopyToPartialAsync(wimPath, source, progress, cancellationToken).ConfigureAwait(false);
        var previous = PreviousImagePath(source);
        string? archived = null;
        try
        {
            if (File.Exists(previous))
            {
                var earlier = EarlierImagePaths(source);
                var next = earlier.Count == 0 ? 1 : int.Parse(Path.GetExtension(earlier[^1]).AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) + 1;
                archived = $"{previous}.{next.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                File.Move(previous, archived);
            }

            log($"Keeping the current image as {previous}");
            File.Move(source, previous);
            try
            {
                File.Move(staged, source);
            }
            catch
            {
                File.Move(previous, source);
                throw;
            }
        }
        catch
        {
            if (archived is not null && !File.Exists(previous))
            {
                File.Move(archived, previous);
            }

            // If the source could not be put back, the staged copy is the only complete new image; leave it for a person.
            if (File.Exists(source))
            {
                File.Delete(staged);
            }

            throw;
        }

        return previous;
    }

    private async Task<IReadOnlyList<DistributionPointGroup>> ResolveGroupsAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return [];
        }

        var all = await connection.GetDistributionPointGroupsAsync(cancellationToken).ConfigureAwait(false);
        return names.Select(name => all.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ConfigMgrException($"Distribution point group \"{name}\" not found. Groups: {string.Join(", ", all.Select(g => g.Name))}"))
            .ToList();
    }

    private async Task<IReadOnlyList<DistributionPoint>> ResolvePointsAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return [];
        }

        // Accept FQDNs or short names: "dp01" matches "dp01.contoso.com".
        var all = await connection.GetDistributionPointsAsync(cancellationToken).ConfigureAwait(false);
        return names.Select(name => all.FirstOrDefault(p =>
                    string.Equals(p.ServerName, name, StringComparison.OrdinalIgnoreCase)
                    || p.ServerName.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
                ?? throw new ConfigMgrException($"Distribution point \"{name}\" not found."))
            .ToList();
    }

    /// <summary>Copies to a temporary name and renames, so a failed copy never leaves a partial image under the real name.</summary>
    private static async Task CopyAsync(string source, string destination, bool overwrite, IProgress<ServicingProgress>? progress, CancellationToken cancellationToken)
    {
        var partial = await CopyToPartialAsync(source, destination, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(partial, destination, overwrite);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    /// <summary>Copies <paramref name="source"/> beside <paramref name="destination"/> under a temporary name, which it returns.</summary>
    private static async Task<string> CopyToPartialAsync(string source, string destination, IProgress<ServicingProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".familiar-partial";
        var operation = $"Copy to {destination}";

        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[4 << 20];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(new ServicingProgress(operation, 100.0 * copied / input.Length));
            }
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return partial;
    }
}
