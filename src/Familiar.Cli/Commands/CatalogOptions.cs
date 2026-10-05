using System.CommandLine;
using System.Globalization;
using Familiar.Core.Releases;

namespace Familiar.Cli.Commands;

/// <summary>Options shared by commands that need release data.</summary>
internal static class CatalogOptions
{
    public static Option<bool> Offline { get; } = new("--offline")
    {
        Description = "Use cached or bundled release data without contacting the network.",
    };

    public static Option<bool> Refresh { get; } = new("--refresh")
    {
        Description = "Download fresh release data even if the cache is current.",
    };

    public static async Task<ReleaseCatalog> LoadAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Familiar/" + typeof(CatalogOptions).Assembly.GetName().Version);

        var source = new ReleaseDataSource(http, new ReleaseDataSourceOptions
        {
            AllowNetwork = !parseResult.GetValue(Offline),
        });

        var result = await source.LoadAsync(parseResult.GetValue(Refresh), cancellationToken);
        foreach (var warning in result.Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        var collected = result.Catalog.CollectedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "unknown";
        Console.Error.WriteLine($"Release data: {result.Origin.ToString().ToLowerInvariant()}, updates collected {collected}");
        return result.Catalog;
    }
}
