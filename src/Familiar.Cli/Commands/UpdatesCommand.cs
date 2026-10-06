using System.CommandLine;
using Familiar.Core.Releases;
using Familiar.Core.Updates;

namespace Familiar.Cli.Commands;

internal static class UpdatesCommand
{
    public static Command Create()
    {
        var release = new Option<string>("--release")
        {
            Description = "Release, e.g. \"Windows 11 25H2\" or just 25H2.",
            Required = true,
        };

        var architecture = new Option<string>("--arch")
        {
            Description = "x64 or arm64.",
            DefaultValueFactory = _ => "x64",
        };
        architecture.AcceptOnlyFromAmong("x64", "arm64");

        var noDotNet = new Option<bool>("--no-dotnet") { Description = "Skip the .NET Framework cumulative update." };
        var noSafeOS = new Option<bool>("--no-safe-os") { Description = "Skip the Safe OS Dynamic Update for WinRE (saved in a SafeOS subfolder)." };
        var preview = new Option<bool>("--preview") { Description = "Allow preview (non-security) updates." };
        var folder = new Option<DirectoryInfo>("--folder") { Description = "Where to save updates.", Required = true };

        var list = new Command("list", "Show which updates would be downloaded.")
        {
            release, architecture, noDotNet, noSafeOS, preview, CatalogOptions.Offline, CatalogOptions.Refresh,
        };
        list.SetAction((parseResult, cancellationToken) => RunAsync(parseResult, download: false, cancellationToken));

        var download = new Command("download", "Download the latest updates from the Microsoft Update Catalog.")
        {
            release, architecture, folder, noDotNet, noSafeOS, preview, CatalogOptions.Refresh,
        };
        download.SetAction((parseResult, cancellationToken) => RunAsync(parseResult, download: true, cancellationToken));

        return new Command("updates", "Find and download cumulative updates.") { list, download };

        async Task<int> RunAsync(ParseResult parseResult, bool download, CancellationToken cancellationToken)
        {
            var catalog = await CatalogOptions.LoadAsync(parseResult, cancellationToken);
            var name = parseResult.GetValue(release)!;
            var match = FindRelease(catalog, name);
            if (match is null)
            {
                Console.Error.WriteLine($"Unknown release '{name}'. Run 'familiar releases' to see the names.");
                return ExitCodes.Invalid;
            }

            using var http = Http.Create();
            var downloader = new UpdateDownloader(http, catalog);
            var arch = parseResult.GetValue(architecture)!;

            try
            {
                var selection = await downloader.SelectAsync(
                    match, arch, !parseResult.GetValue(noDotNet), parseResult.GetValue(preview), !parseResult.GetValue(noSafeOS), cancellationToken);
                Console.WriteLine(selection.CumulativeUpdate.Title);
                if (selection.DotNet is { } dotNet)
                {
                    Console.WriteLine(dotNet.Title);
                }

                if (selection.SafeOS is { } safeOS)
                {
                    Console.WriteLine(safeOS.Title);
                }

                if (!download)
                {
                    return ExitCodes.Success;
                }

                var progress = new ConsoleProgress();
                var result = await downloader.DownloadAsync(selection, parseResult.GetValue(folder)!.FullName, progress, cancellationToken);
                foreach (var file in result.Files)
                {
                    progress.Log($"{(file.AlreadyPresent ? "Already have" : "Downloaded")} {file.Path}");
                }

                foreach (var removed in result.Removed)
                {
                    progress.Log($"Removed superseded {removed}");
                }

                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or NotSupportedException)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        }
    }

    private static WindowsRelease? FindRelease(ReleaseCatalog catalog, string name)
    {
        var trimmed = name.Trim();
        return catalog.Releases.FirstOrDefault(r => string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? catalog.Releases
                .Where(r => r.OsType == WindowsOsType.Client && string.Equals(r.Version, trimmed, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.MajorVersion)
                .FirstOrDefault();
    }
}
