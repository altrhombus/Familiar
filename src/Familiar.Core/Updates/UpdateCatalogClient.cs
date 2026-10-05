using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Familiar.Core.Updates;

/// <summary>One search result row from the Microsoft Update Catalog.</summary>
public sealed record CatalogEntry(string UpdateId, string Title, string Products, string Classification, DateOnly? LastUpdated, long? SizeBytes);

/// <summary>A downloadable file behind a catalog entry. <paramref name="Sha1"/> is the catalog's base64 SHA-1 digest.</summary>
public sealed record CatalogFile(Uri Url, string FileName, string? Sha1);

/// <summary>
/// Minimal client for https://www.catalog.update.microsoft.com. The catalog has no API, so this reads the same
/// search page and download dialog a browser uses (as the MSCatalog module and OSDBuilder do). Parsing is kept
/// small and isolated so a site change breaks one class, with a clear error.
/// </summary>
public sealed partial class UpdateCatalogClient(HttpClient http)
{
    private static readonly Uri BaseUri = new("https://www.catalog.update.microsoft.com/");

    public async Task<IReadOnlyList<CatalogEntry>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "Search.aspx?q=" + Uri.EscapeDataString(query)));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseSearch(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<CatalogFile>> GetFilesAsync(string updateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updateId);

        var payload = JsonSerializer.Serialize(new[] { new DownloadRequest(0, string.Empty, updateId, updateId) }, CatalogJsonContext.Default.DownloadRequestArray);
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("updateIDs", payload)]);
        using var response = await http.PostAsync(new Uri(BaseUri, "DownloadDialog.aspx"), content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var files = ParseDownloadDialog(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return files.Count > 0
            ? files
            : throw new InvalidOperationException($"The Update Catalog returned no files for update {updateId}; the site may have changed.");
    }

    internal static IReadOnlyList<CatalogEntry> ParseSearch(string page)
    {
        // Cells look like <td ... id="{updateId}_C{column}_R{row}">...</td>; columns 1-6 are
        // title, products, classification, last updated, version and size.
        var rows = new Dictionary<(int Row, string Id), Dictionary<int, string>>();
        foreach (Match cell in CellPattern().Matches(page))
        {
            var key = (int.Parse(cell.Groups["row"].Value, CultureInfo.InvariantCulture), cell.Groups["id"].Value);
            if (!rows.TryGetValue(key, out var columns))
            {
                rows[key] = columns = [];
            }

            columns[int.Parse(cell.Groups["column"].Value, CultureInfo.InvariantCulture)] = Text(cell.Groups["body"].Value);
        }

        return rows.OrderBy(r => r.Key.Row)
            .Select(r => new CatalogEntry(
                r.Key.Id,
                r.Value.GetValueOrDefault(1, string.Empty),
                r.Value.GetValueOrDefault(2, string.Empty),
                r.Value.GetValueOrDefault(3, string.Empty),
                DateOnly.TryParseExact(r.Value.GetValueOrDefault(4), "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                ParseSize(r.Value.GetValueOrDefault(6))))
            .Where(e => e.Title.Length > 0)
            .ToList();
    }

    internal static IReadOnlyList<CatalogFile> ParseDownloadDialog(string page)
    {
        // downloadInformation[0].files[1].url = '...'; .fileName = '...'; .digest = '...';
        var files = new Dictionary<int, Dictionary<string, string>>();
        foreach (Match property in FilePropertyPattern().Matches(page))
        {
            var index = int.Parse(property.Groups["file"].Value, CultureInfo.InvariantCulture);
            if (!files.TryGetValue(index, out var values))
            {
                files[index] = values = [];
            }

            values[property.Groups["name"].Value] = property.Groups["value"].Value;
        }

        return files.OrderBy(f => f.Key)
            .Where(f => f.Value.ContainsKey("url"))
            .Select(f => new CatalogFile(
                new Uri(f.Value["url"]),
                f.Value.GetValueOrDefault("fileName") ?? Path.GetFileName(new Uri(f.Value["url"]).LocalPath),
                f.Value.GetValueOrDefault("digest")))
            .ToList();
    }

    private static string Text(string html) =>
        WhitespacePattern().Replace(WebUtility.HtmlDecode(TagPattern().Replace(html, " ")), " ").Trim();

    // "4933.5 MB 5173184334": the display size, then the exact byte count from a hidden span.
    private static long? ParseSize(string? value) =>
        value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() is { } last
        && long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? bytes
            : null;

    [GeneratedRegex(@"id=""(?<id>[0-9a-f-]{36})_C(?<column>\d)_R(?<row>\d+)"">(?<body>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CellPattern();

    [GeneratedRegex(@"downloadInformation\[\d+\]\.files\[(?<file>\d+)\]\.(?<name>url|fileName|digest)\s*=\s*'(?<value>[^']*)'")]
    private static partial Regex FilePropertyPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}

internal sealed record DownloadRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("size")] int Size,
    [property: System.Text.Json.Serialization.JsonPropertyName("languages")] string Languages,
    [property: System.Text.Json.Serialization.JsonPropertyName("uidInfo")] string UidInfo,
    [property: System.Text.Json.Serialization.JsonPropertyName("updateID")] string UpdateId);

[System.Text.Json.Serialization.JsonSerializable(typeof(DownloadRequest[]))]
internal sealed partial class CatalogJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
