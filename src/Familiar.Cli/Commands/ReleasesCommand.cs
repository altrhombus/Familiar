using System.CommandLine;
using System.Globalization;
using Familiar.Core.Releases;

namespace Familiar.Cli.Commands;

internal static class ReleasesCommand
{
    public static Command Create()
    {
        var all = new Option<bool>("--all")
        {
            Description = "Include releases that are out of support.",
        };

        var command = new Command("releases", "List Windows releases, their servicing baseline and latest cumulative update.")
        {
            all,
            CatalogOptions.Offline,
            CatalogOptions.Refresh,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var catalog = await CatalogOptions.LoadAsync(parseResult, cancellationToken);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var showAll = parseResult.GetValue(all);

            Console.WriteLine($"{"Release",-34} {"Build",-6} {"Baseline",-8} {"Latest update",-34} Support ends");
            foreach (var release in catalog.Releases)
            {
                // Enterprise dates are the longest-lived client channel and what image builders usually target.
                var lifecycle = catalog.GetLifecycle(release, release.OsType == WindowsOsType.Client ? "Enterprise" : null);
                if (!showAll && lifecycle is not null && !lifecycle.IsSupportedOn(today))
                {
                    continue;
                }

                var latest = catalog.GetLatestUpdate(release)?.ToString() ?? "-";
                var ends = lifecycle?.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
                Console.WriteLine($"{release.Name,-34} {release.Build,-6} {release.ServicingBaselineBuild,-8} {latest,-34} {ends}");
            }

            return 0;
        });

        return command;
    }
}
