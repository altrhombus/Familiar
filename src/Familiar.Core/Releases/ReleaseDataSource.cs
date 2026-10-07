using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Familiar.Core.Releases;

public sealed class ReleaseDataSourceOptions
{
    public Uri BaseUri { get; init; } = new("https://raw.githubusercontent.com/altrhombus/DataForGeeks/main/content/ms/win/");

    public string CacheDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Familiar", "ReleaseData");

    /// <summary>How old the cache may get before a refresh is attempted. Upstream updates several times a day.</summary>
    public TimeSpan RefreshAfter { get; init; } = TimeSpan.FromHours(12);

    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Set false for air-gapped build machines; the cache and bundled snapshot are used instead.</summary>
    public bool AllowNetwork { get; init; } = true;
}

public enum ReleaseDataOrigin
{
    /// <summary>At least one file was downloaded or revalidated during this load.</summary>
    Network,

    /// <summary>Loaded from the local cache without contacting the network.</summary>
    Cache,

    /// <summary>Loaded from the snapshot compiled into Familiar.</summary>
    Snapshot,
}

public sealed record ReleaseCatalogLoadResult(ReleaseCatalog Catalog, ReleaseDataOrigin Origin, IReadOnlyList<string> Warnings);

/// <summary>
/// Loads release data from DataForGeeks, falling back to a local cache and then to the snapshot bundled at
/// build time. Network problems never fail a load; they surface as warnings.
/// </summary>
public sealed class ReleaseDataSource(HttpClient http, ReleaseDataSourceOptions? options = null)
{
    private readonly ReleaseDataSourceOptions _options = options ?? new ReleaseDataSourceOptions();

    public async Task<ReleaseCatalogLoadResult> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var origin = ReleaseDataOrigin.Snapshot;
        var streams = new Dictionary<ReleaseDataFile, Stream>();

        try
        {
            foreach (var file in Enum.GetValues<ReleaseDataFile>())
            {
                var (stream, fileOrigin) = await OpenAsync(file, forceRefresh, warnings, cancellationToken).ConfigureAwait(false);
                streams[file] = stream;
                origin = (ReleaseDataOrigin)Math.Min((int)origin, (int)fileOrigin);
            }

            try
            {
                return new(ReleaseCatalog.Create(ReleaseDataSet.Parse(streams)), origin, warnings);
            }
            catch (JsonException ex) when (origin != ReleaseDataOrigin.Snapshot)
            {
                warnings.Add($"Cached release data could not be read ({ex.Message}); using the bundled snapshot.");
                return new(LoadSnapshot(), ReleaseDataOrigin.Snapshot, warnings);
            }
        }
        finally
        {
            foreach (var stream in streams.Values)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Builds a catalog from the bundled snapshot only. Never touches disk or network.</summary>
    public static ReleaseCatalog LoadSnapshot()
    {
        var streams = Enum.GetValues<ReleaseDataFile>().ToDictionary(f => f, OpenSnapshot);
        try
        {
            return ReleaseCatalog.Create(ReleaseDataSet.Parse(streams));
        }
        finally
        {
            foreach (var stream in streams.Values)
            {
                stream.Dispose();
            }
        }
    }

    private async Task<(Stream Stream, ReleaseDataOrigin Origin)> OpenAsync(
        ReleaseDataFile file, bool forceRefresh, List<string> warnings, CancellationToken cancellationToken)
    {
        var name = ReleaseDataSet.FileName(file);
        var cachePath = Path.Combine(_options.CacheDirectory, name);
        var cached = File.Exists(cachePath);
        var stale = !cached || DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) > _options.RefreshAfter;
        var refreshed = false;

        if (_options.AllowNetwork && (forceRefresh || stale))
        {
            try
            {
                refreshed = await RefreshAsync(name, cachePath, cancellationToken).ConfigureAwait(false);
                cached = File.Exists(cachePath);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException
                or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"Could not refresh {name}: {ex.Message}");
            }
        }

        if (cached && !IsOlderThanSnapshot(file, cachePath))
        {
            return (File.OpenRead(cachePath), refreshed ? ReleaseDataOrigin.Network : ReleaseDataOrigin.Cache);
        }

        return (OpenSnapshot(file), ReleaseDataOrigin.Snapshot);
    }

    /// <returns><c>true</c> when the server was reached (new content or 304 Not Modified).</returns>
    private async Task<bool> RefreshAsync(string name, string cachePath, CancellationToken cancellationToken)
    {
        var etagPath = cachePath + ".etag";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri, name));
        if (File.Exists(cachePath) && File.Exists(etagPath)
            && EntityTagHeaderValue.TryParse(await File.ReadAllTextAsync(etagPath, cancellationToken).ConfigureAwait(false), out var etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.NetworkTimeout);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
            return true;
        }

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        Validate(content, name);

        Directory.CreateDirectory(_options.CacheDirectory);
        var temp = cachePath + ".tmp";
        await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
        File.Move(temp, cachePath, overwrite: true);

        if (response.Headers.ETag is { } newTag)
        {
            await File.WriteAllTextAsync(etagPath, newTag.ToString(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            File.Delete(etagPath);
        }

        return true;
    }

    // Only replace the cache with something shaped like a DataForGeeks file.
    private static void Validate(byte[] content, string name)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{name} has no data array.");
        }
    }

    // A cache written before Familiar was updated can be older than the snapshot that shipped with it.
    private static bool IsOlderThanSnapshot(ReleaseDataFile file, string cachePath)
    {
        try
        {
            DateTimeOffset? cacheCollected;
            using (var cache = File.OpenRead(cachePath))
            {
                cacheCollected = ReleaseDataSet.ReadCollectedAt(cache);
            }

            using var snapshot = OpenSnapshot(file);
            var snapshotCollected = ReleaseDataSet.ReadCollectedAt(snapshot);
            return cacheCollected < snapshotCollected;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static Stream OpenSnapshot(ReleaseDataFile file) =>
        typeof(ReleaseDataSource).Assembly.GetManifestResourceStream($"Familiar.Core.Releases.Snapshot.{ReleaseDataSet.FileName(file)}")
        ?? throw new InvalidOperationException($"Release snapshot {ReleaseDataSet.FileName(file)} is missing from the build.");
}
