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

        var command = new Command("info", "List the indexes in a WIM or ESD file.")
        {
            image,
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
            foreach (var index in dism.GetIndexes(file.FullName))
            {
                var release = catalog.Resolve(index.Version.Build, index.InstallationType);

                Console.WriteLine($"[{index.Index}] {index.Name}");
                Console.WriteLine($"    Edition:   {index.EditionId} / {index.InstallationType} / {index.Architecture}");
                Console.WriteLine($"    Language:  {index.DefaultLanguage ?? "unknown"}");

                if (release is null)
                {
                    Console.WriteLine($"    Release:   unknown build {index.Version} (try --refresh)");
                    continue;
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

            return ExitCodes.Success;
        });

        return command;
    }
}
