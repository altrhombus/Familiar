using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Familiar.Core.Releases;
using Familiar.Core.Servicing;

namespace Familiar.Core.Updates;

/// <summary>
/// What to fetch for one release: the cumulative update (with checkpoints), and optionally .NET and the Safe OS
/// Dynamic Update for WinRE.
/// </summary>
public sealed record UpdateSelection(CatalogEntry CumulativeUpdate, CatalogEntry? DotNet, CatalogEntry? SafeOS = null);

public sealed record DownloadedFile(string Path, bool AlreadyPresent);

/// <param name="Removed">Superseded files from Familiar's previous download that were deleted.</param>
public sealed record DownloadResult(IReadOnlyList<DownloadedFile> Files, IReadOnlyList<string> Removed);

/// <summary>Records which files in an updates folder Familiar downloaded, so it only ever prunes its own.</summary>
internal static class DownloadManifest
{
    public static IReadOnlyList<string> Read(string folder)
    {
        var path = Path.Combine(folder, UpdateDownloader.ManifestFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), ManifestJsonContext.Default.StringArray) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // A damaged manifest only means nothing is pruned this time.
            return [];
        }
    }

    public static void Write(string folder, IEnumerable<string> files) =>
        File.WriteAllText(
            Path.Combine(folder, UpdateDownloader.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(files.ToArray(), ManifestJsonContext.Default.StringArray));
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class ManifestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Finds and downloads the latest updates for a release from the Microsoft Update Catalog.</summary>
public sealed class UpdateDownloader(HttpClient http, ReleaseCatalog catalog)
{
    private readonly UpdateCatalogClient _client = new(http);

    /// <summary>
    /// Picks the latest cumulative update the release data knows for <paramref name="release"/>, then the catalog
    /// entry matching the release and architecture. The KB comes from DataForGeeks so the choice does not depend
    /// on catalog search ranking.
    /// </summary>
    public async Task<UpdateSelection> SelectAsync(
        WindowsRelease release, string architecture, bool includeDotNet, bool includePreview = false, bool includeSafeOS = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.OsType != WindowsOsType.Client)
        {
            throw new NotSupportedException("Downloading updates is supported for Windows client releases so far.");
        }

        var latest = catalog.GetLatestUpdate(release, includePreview)
            ?? throw new InvalidOperationException($"The release data lists no cumulative update for {release.Name}.");

        var results = await _client.SearchAsync(latest.KbArticle, cancellationToken).ConfigureAwait(false);
        var cumulative = results.FirstOrDefault(e => IsCumulativeUpdateFor(e.Title, release, architecture, latest.KbArticle))
            ?? throw new InvalidOperationException(
                $"The Update Catalog has no {architecture} entry for {latest.KbArticle} on {release.Name}. Found: {string.Join("; ", results.Select(r => r.Title))}");

        CatalogEntry? dotNet = null;
        if (includeDotNet)
        {
            var query = $"Cumulative Update for .NET Framework Windows {release.MajorVersion} version {release.Version} {architecture}";
            dotNet = (await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false))
                .Where(e => IsDotNetUpdateFor(e.Title, release, architecture) && (includePreview || !IsPreview(e.Title)))
                .OrderByDescending(e => e.LastUpdated)
                .FirstOrDefault();
        }

        CatalogEntry? safeOS = null;
        if (includeSafeOS)
        {
            // Some months have two; the most recently published one supersedes the other.
            var query = $"Safe OS Dynamic Update Windows {release.MajorVersion} {release.Version} {architecture}";
            safeOS = (await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false))
                .Where(e => IsSafeOSUpdateFor(e.Title, release, architecture))
                .OrderByDescending(e => e.LastUpdated)
                .ThenByDescending(e => e.Title, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"The Update Catalog has no {architecture} Safe OS Dynamic Update for {release.Name}.");
        }

        return new UpdateSelection(cumulative, dotNet, safeOS);
    }

    /// <summary>Name of the file recording what Familiar downloaded into a folder.</summary>
    public const string ManifestFileName = "familiar-downloads.json";

    /// <summary>
    /// Downloads every file for the selected updates into <paramref name="folder"/>, verifying SHA-1, then removes
    /// files from Familiar's previous download that the new selection supersedes. Cumulative updates replace each
    /// other, and every file left in the folder is installed, so stale ones only add build time. Files Familiar
    /// did not download are never removed. The Safe OS update goes in the SafeOS subfolder, which builds apply to
    /// WinRE only.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        UpdateSelection selection, string folder, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var downloaded = new List<DownloadedFile>();
        var removed = new List<string>();
        await DownloadToAsync(folder, [selection.CumulativeUpdate, selection.DotNet]).ConfigureAwait(false);
        if (selection.SafeOS is { } safeOS)
        {
            await DownloadToAsync(Path.Combine(folder, Recipes.UpdatesSection.SafeOSFolderName), [safeOS]).ConfigureAwait(false);
        }

        return new DownloadResult(downloaded, removed);

        async Task DownloadToAsync(string target, CatalogEntry?[] entries)
        {
            Directory.CreateDirectory(target);
            var current = new List<string>();
            foreach (var entry in entries.OfType<CatalogEntry>())
            {
                foreach (var file in await _client.GetFilesAsync(entry.UpdateId, cancellationToken).ConfigureAwait(false))
                {
                    var result = await DownloadFileAsync(file, target, progress, cancellationToken).ConfigureAwait(false);
                    downloaded.Add(result);
                    current.Add(Path.GetFileName(result.Path));
                }
            }

            removed.AddRange(PruneSuperseded(target, current));
            DownloadManifest.Write(target, current);
        }
    }

    /// <summary>
    /// Deletes the files of Familiar's previous download into <paramref name="folder"/> that are not in
    /// <paramref name="current"/>. Builds run elevated, so the manifest is not trusted: only plain .msu and .cab
    /// file names directly inside the folder are deleted, never a path that leads elsewhere.
    /// </summary>
    internal static IReadOnlyList<string> PruneSuperseded(string folder, IReadOnlyCollection<string> current)
    {
        var removed = new List<string>();
        foreach (var previous in DownloadManifest.Read(folder).Except(current, StringComparer.OrdinalIgnoreCase))
        {
            if (ChildPath(folder, previous) is { } path
                && (path.EndsWith(".msu", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
                && File.Exists(path))
            {
                File.Delete(path);
                removed.Add(path);
            }
        }

        return removed;
    }

    /// <summary>
    /// The full path of <paramref name="fileName"/> directly inside <paramref name="folder"/>, or null when it is not a
    /// plain file name (empty, rooted, a relative path, a stream, or anything else that would resolve elsewhere).
    /// </summary>
    internal static string? ChildPath(string folder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".."
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.GetFullPath(folder);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        return string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>Downloads one catalog file into <paramref name="folder"/>, refusing any file it cannot verify.</summary>
    internal async Task<DownloadedFile> DownloadFileAsync(CatalogFile file, string folder, IProgress<ServicingProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var target = ChildPath(folder, file.FileName)
            ?? throw new InvalidDataException($"The Update Catalog listed a file name that is not a plain file name: \"{file.FileName}\".");

        // Fail closed: without a digest nothing can be verified, so a change in the catalog page must not let files through.
        if (!IsSha1Digest(file.Sha1))
        {
            throw new InvalidDataException(
                $"The Update Catalog listed no usable SHA-1 digest for {file.FileName}, so it cannot be verified. "
                + "Try again later, or download it yourself into the updates folder and set updates.download to false.");
        }

        if (File.Exists(target) && await MatchesAsync(target, file.Sha1, cancellationToken).ConfigureAwait(false))
        {
            return new DownloadedFile(target, AlreadyPresent: true);
        }

        var operation = $"Download {file.FileName}";
        progress?.Report(ServicingProgress.Started(operation));

        var partial = target + ".partial";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using (var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

            var buffer = new byte[1 << 20];
            long written = 0;
            var lastReported = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                written += read;

                if (total > 0 && (int)(1000 * written / total.Value) is var permille && permille != lastReported)
                {
                    lastReported = permille;
                    progress?.Report(new ServicingProgress(operation, permille / 10.0));
                }
            }
        }

        var actual = Convert.ToBase64String(hash.GetHashAndReset());
        if (!string.Equals(actual, file.Sha1, StringComparison.Ordinal))
        {
            File.Delete(partial);
            throw new InvalidDataException($"{file.FileName} failed verification (SHA-1 {actual}, expected {file.Sha1}). Try the download again.");
        }

        File.Move(partial, target, overwrite: true);
        return new DownloadedFile(target, AlreadyPresent: false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is the only digest the Update Catalog publishes. It is checked against the catalog's own listing; no practical second-preimage attack on SHA-1 is known.")]
    private static async Task<bool> MatchesAsync(string path, string expectedSha1, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var actual = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToBase64String(actual), expectedSha1, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="digest"/> is a base64 SHA-1 digest (20 bytes), as the catalog lists them.</summary>
    internal static bool IsSha1Digest([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? digest)
    {
        Span<byte> bytes = stackalloc byte[21];
        return digest is not null && Convert.TryFromBase64String(digest, bytes, out var written) && written == 20;
    }

    // "2026-09 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5129195) (26200.9457)"
    // "2021-06 Cumulative Update for Windows 10 Version 1809 for x64-based Systems (KB5003646)"
    internal static bool IsCumulativeUpdateFor(string title, WindowsRelease release, string architecture, string kb) =>
        Regex.IsMatch(title,
            $@"\bCumulative Update for Windows {release.MajorVersion},? version {Regex.Escape(release.Version)} for {Regex.Escape(architecture)}-based Systems\b.*\({Regex.Escape(kb)}\)",
            RegexOptions.IgnoreCase)
        && !title.Contains("Dynamic", StringComparison.OrdinalIgnoreCase);

    // "2026-09 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 25H2 for x64 (KB5126052)"
    internal static bool IsDotNetUpdateFor(string title, WindowsRelease release, string architecture) =>
        Regex.IsMatch(title,
            $@"\bCumulative Update (Preview )?for \.NET Framework .* for Windows {release.MajorVersion},? version {Regex.Escape(release.Version)} for {Regex.Escape(architecture)}\b",
            RegexOptions.IgnoreCase);

    // "2026-09 Safe OS Dynamic Update for Windows 11, version 25H2 for x64-based Systems (KB5125758)"
    // "2026-04 Safe OS Dynamic Update for Windows 11 Version 25H2 for x64-based Systems (KB5084812)"
    internal static bool IsSafeOSUpdateFor(string title, WindowsRelease release, string architecture) =>
        Regex.IsMatch(title,
            $@"\bSafe OS Dynamic Update for Windows {release.MajorVersion},? version {Regex.Escape(release.Version)} for {Regex.Escape(architecture)}-based Systems\b",
            RegexOptions.IgnoreCase);

    private static bool IsPreview(string title) => title.Contains("Preview", StringComparison.OrdinalIgnoreCase);
}
