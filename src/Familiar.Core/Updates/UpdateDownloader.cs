using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Familiar.Core.Releases;
using Familiar.Core.Servicing;

namespace Familiar.Core.Updates;

/// <summary>What to fetch for one release: the cumulative update (with checkpoints) and optionally .NET.</summary>
public sealed record UpdateSelection(CatalogEntry CumulativeUpdate, CatalogEntry? DotNet);

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
        WindowsRelease release, string architecture, bool includeDotNet, bool includePreview = false, CancellationToken cancellationToken = default)
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

        return new UpdateSelection(cumulative, dotNet);
    }

    /// <summary>Name of the file recording what Familiar downloaded into a folder.</summary>
    public const string ManifestFileName = "familiar-downloads.json";

    /// <summary>
    /// Downloads every file for the selected updates into <paramref name="folder"/>, verifying SHA-1, then removes
    /// files from Familiar's previous download that the new selection supersedes. Cumulative updates replace each
    /// other, and every file left in the folder is installed, so stale ones only add build time. Files Familiar
    /// did not download are never removed.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        UpdateSelection selection, string folder, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Directory.CreateDirectory(folder);

        var downloaded = new List<DownloadedFile>();
        foreach (var entry in new[] { selection.CumulativeUpdate, selection.DotNet }.OfType<CatalogEntry>())
        {
            foreach (var file in await _client.GetFilesAsync(entry.UpdateId, cancellationToken).ConfigureAwait(false))
            {
                downloaded.Add(await DownloadFileAsync(file, folder, progress, cancellationToken).ConfigureAwait(false));
            }
        }

        var current = downloaded.Select(d => Path.GetFileName(d.Path)).ToList();
        var removed = new List<string>();
        foreach (var previous in DownloadManifest.Read(folder).Except(current, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, previous);
            if (File.Exists(path))
            {
                File.Delete(path);
                removed.Add(path);
            }
        }

        DownloadManifest.Write(folder, current);
        return new DownloadResult(downloaded, removed);
    }

    private async Task<DownloadedFile> DownloadFileAsync(CatalogFile file, string folder, IProgress<ServicingProgress>? progress, CancellationToken cancellationToken)
    {
        var target = Path.Combine(folder, file.FileName);
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
        if (file.Sha1 is not null && !string.Equals(actual, file.Sha1, StringComparison.Ordinal))
        {
            File.Delete(partial);
            throw new InvalidDataException($"{file.FileName} failed verification (SHA-1 {actual}, expected {file.Sha1}). Try the download again.");
        }

        File.Move(partial, target, overwrite: true);
        return new DownloadedFile(target, AlreadyPresent: false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 is the digest the Update Catalog publishes; this is an integrity check over HTTPS, not a security boundary.")]
    private static async Task<bool> MatchesAsync(string path, string? expectedSha1, CancellationToken cancellationToken)
    {
        if (expectedSha1 is null)
        {
            return true;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var actual = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToBase64String(actual), expectedSha1, StringComparison.Ordinal);
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

    private static bool IsPreview(string title) => title.Contains("Preview", StringComparison.OrdinalIgnoreCase);
}
