using System.Text.Json.Serialization;

namespace Familiar.Core.Releases.DataForGeeks;

// Wire shapes for https://github.com/altrhombus/DataForGeeks (content/ms/win, apiVersion v2).
// Metadata is camelCase; records are snake_case.

internal sealed record Envelope<T>(
    [property: JsonPropertyName("metadata")] Metadata Metadata,
    [property: JsonPropertyName("data")] IReadOnlyList<T> Data);

internal sealed record Metadata(
    [property: JsonPropertyName("dataset")] string? Dataset,
    [property: JsonPropertyName("apiVersion")] string? ApiVersion,
    [property: JsonPropertyName("recordCount")] int RecordCount,
    [property: JsonPropertyName("lastCollected")] DateTimeOffset? LastCollected);

internal sealed record ReleaseRecord(
    string OsType,
    int MajorVersion,
    string WindowsVersion,
    string OsBuild,
    string? BaseBuild = null);

internal sealed record BuildNumberRecord(
    string Build,
    string OsType,
    int MajorVersion,
    string WindowsVersion,
    string ReleaseDate,
    string KbArticle,
    string ReleaseType,
    bool IsExpired,
    string? ArticleUrl);

internal sealed record ClientLifecycleRecord(
    string Version,
    string Sku,
    string StartDate,
    string EndDate);

internal sealed record LtscLifecycleRecord(
    string Version,
    string ServicingOption,
    string Build,
    string StartDate,
    string MainstreamEndDate,
    string ExtendedEndDate);

internal sealed record ServerLifecycleRecord(
    string Version,
    string Tier,
    string StartDate,
    string MainstreamEndDate,
    string ExtendedEndDate);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Envelope<ReleaseRecord>))]
[JsonSerializable(typeof(Envelope<BuildNumberRecord>))]
[JsonSerializable(typeof(Envelope<ClientLifecycleRecord>))]
[JsonSerializable(typeof(Envelope<LtscLifecycleRecord>))]
[JsonSerializable(typeof(Envelope<ServerLifecycleRecord>))]
internal sealed partial class DataForGeeksJsonContext : JsonSerializerContext;
