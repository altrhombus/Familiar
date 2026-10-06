using System.CommandLine;
using Familiar.Core;
using Familiar.Core.Images;

namespace Familiar.Cli.Commands;

internal static class InfoCommand
{
    public static Command Create()
    {
        var image = new Argument<FileInfo>("image")
        {
            Description = "Path to a WIM or ESD file.",
        };

        var winRE = new Option<bool>("--winre")
        {
            Description = "Also show each index's WinRE build, languages and size (mounts the index read-only; slower).",
        };

        var command = new Command("info", "List the indexes in a WIM or ESD file.")
        {
            image,
            winRE,
            CatalogOptions.Offline,
            CatalogOptions.Refresh,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(image)!;
            if (!file.Exists)
            {
                Console.Error.WriteLine($"Image not found: {file.FullName}");
                return ExitCodes.NotFound;
            }

            if (!ProcessElevation.IsElevated)
            {
                Console.Error.WriteLine("Reading image metadata uses the DISM API, which requires an elevated prompt.");
                return ExitCodes.NotElevated;
            }

            var catalog = await CatalogOptions.LoadAsync(parseResult, cancellationToken);
            var today = DateOnly.FromDateTime(DateTime.Today);

            using var dism = new DismHost();
            IReadOnlyList<ImageIndexInfo> indexes;
            try
            {
                indexes = dism.GetIndexes(file.FullName);
            }
            catch (Exception ex) when (DismErrors.Describe(ex) is { } description)
            {
                Console.Error.WriteLine($"error: {description}");
                return ExitCodes.Failure;
            }

            foreach (var index in indexes)
            {
                await WriteIndexAsync(index);
            }

            return ExitCodes.Success;

            async Task WriteIndexAsync(ImageIndexInfo index)
            {
                var release = catalog.Resolve(index.Version.Build, index.InstallationType);

                Console.WriteLine($"[{index.Index}] {index.Name}");
                Console.WriteLine($"    Edition:   {index.EditionId} / {index.InstallationType} / {index.Architecture}");
                var others = index.Languages.Where(l => !string.Equals(l, index.DefaultLanguage, StringComparison.OrdinalIgnoreCase)).ToList();
                var also = others.Count > 0 ? $" (also {string.Join(", ", others)})" : string.Empty;
                Console.WriteLine($"    Language:  {index.DefaultLanguage ?? "unknown"}{also}");

                if (parseResult.GetValue(winRE))
                {
                    await WriteWinREAsync(index);
                }

                if (release is null)
                {
                    Console.WriteLine($"    Release:   unknown build {index.Version} (try --refresh)");
                    return;
                }

                var baseline = release.SharesBaseline ? $", servicing baseline {release.ServicingBaselineBuild}" : string.Empty;
                Console.WriteLine($"    Release:   {release.Name} (build {index.Version}{baseline})");

                var installed = catalog.FindInstalledUpdate(release, index.Version.Revision);
                var latest = catalog.GetLatestUpdate(release);
                Console.WriteLine($"    Patched:   {installed?.ToString() ?? $"revision {index.Version.Revision}"}");
                if (latest is not null && latest.Revision > index.Version.Revision)
                {
                    Console.WriteLine($"    Available: {latest}");
                }

                var lifecycle = catalog.GetLifecycle(release, index.EditionId);
                if (lifecycle?.EndDate is { } end)
                {
                    var state = lifecycle.IsSupportedOn(today) ? "supported until" : "OUT OF SUPPORT since";
                    Console.WriteLine($"    Support:   {state} {end:yyyy-MM-dd} ({lifecycle.Channel})");
                }
            }

            async Task WriteWinREAsync(ImageIndexInfo index)
            {
                try
                {
                    var recovery = await dism.GetWinREAsync(file.FullName, index.Index, cancellationToken);
                    if (recovery is null)
                    {
                        Console.WriteLine("    WinRE:     none");
                        return;
                    }

                    var others = recovery.Languages.Where(l => !string.Equals(l, recovery.DefaultLanguage, StringComparison.OrdinalIgnoreCase)).ToList();
                    var also = others.Count > 0 ? $" (also {string.Join(", ", others)})" : string.Empty;
                    Console.WriteLine($"    WinRE:     build {recovery.Version.Build}.{recovery.Version.Revision}, {recovery.SizeBytes >> 20} MB, {recovery.DefaultLanguage ?? "unknown"}{also}");
                }
                catch (Exception ex) when (DismErrors.Describe(ex) is { } description)
                {
                    Console.WriteLine($"    WinRE:     could not read ({description})");
                }
            }
        });

        return command;
    }
}
