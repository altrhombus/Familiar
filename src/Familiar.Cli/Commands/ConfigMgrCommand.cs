using System.CommandLine;
using Familiar.Core.ConfigMgr;
using Familiar.Core.Recipes;

namespace Familiar.Cli.Commands;

internal static class ConfigMgrCommand
{
    public static Command Create() => new("configmgr", "Configuration Manager: check a site and publish images.")
    {
        CreateInfo(),
        CreatePublish(),
        CreateRollback(),
    };

    private static Command CreateRollback()
    {
        var recipePath = new Argument<FileInfo>("recipe") { Description = "Recipe whose configMgr.packageId to roll back." };
        var command = new Command("rollback", "Restore the image an update replaced (kept as .previous) and refresh distribution points.") { recipePath };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var recipe = await RecipeCommand.LoadAsync(parseResult.GetValue(recipePath)!, cancellationToken);
            if (recipe?.ConfigMgr is not { PackageId: { } packageId } section)
            {
                Console.Error.WriteLine("The recipe needs a configMgr section with packageId to roll back.");
                return ExitCodes.Invalid;
            }

            var progress = new ConsoleProgress();
            try
            {
                await using var connection = await ConfigMgrConnector.ConnectAsync(section.SiteServer, section.SiteCode, section.Transport, progress.Log, cancellationToken);
                await new ConfigMgrPublisher(connection).RollbackAsync(packageId, progress.Log, cancellationToken);
                progress.Log($"Rolled back {packageId}");
                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is ConfigMgrException or IOException or UnauthorizedAccessException)
            {
                progress.Log($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }

    private static Command CreateInfo()
    {
        var siteServer = new Option<string>("--site-server") { Description = "FQDN of the SMS Provider.", Required = true };
        var siteCode = new Option<string?>("--site-code") { Description = "Site code (discovered when omitted)." };
        var transport = new Option<ConfigMgrTransport>("--transport") { Description = "Auto, AdminService or Wmi.", DefaultValueFactory = _ => ConfigMgrTransport.Auto };

        var command = new Command("info", "Connect to a site and list OS image packages and distribution point groups.")
        {
            siteServer,
            siteCode,
            transport,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                await using var connection = await ConfigMgrConnector.ConnectAsync(
                    parseResult.GetValue(siteServer)!, parseResult.GetValue(siteCode), parseResult.GetValue(transport), Console.Error.WriteLine, cancellationToken);

                Console.WriteLine($"Site {connection.SiteCode} via {connection.Transport}");
                Console.WriteLine();
                Console.WriteLine("OS image packages:");
                foreach (var package in (await connection.GetImagePackagesAsync(cancellationToken)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {package.PackageId}  {package.Name} {package.Version}  [{package.ImageOSVersion}]  content v{package.SourceVersion}  {package.SourcePath}");
                }

                Console.WriteLine();
                Console.WriteLine("Distribution point groups:");
                foreach (var group in (await connection.GetDistributionPointGroupsAsync(cancellationToken)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {group.Name} ({group.MemberCount} members)");
                }

                Console.WriteLine();
                Console.WriteLine("Distribution points:");
                foreach (var point in (await connection.GetDistributionPointsAsync(cancellationToken)).OrderBy(p => p.ServerName, StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {point.ServerName} ({point.SiteCode})");
                }

                return ExitCodes.Success;
            }
            catch (ConfigMgrException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }

    private static Command CreatePublish()
    {
        var recipePath = new Argument<FileInfo>("recipe") { Description = "Recipe whose output and configMgr section to use." };
        var wim = new Option<FileInfo?>("--wim") { Description = "Publish this WIM instead of the recipe's output.path." };

        var command = new Command("publish", "Publish an already-built image to Configuration Manager.")
        {
            recipePath,
            wim,
            CatalogOptions.Offline,
            CatalogOptions.Refresh,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var recipe = await RecipeCommand.LoadAsync(parseResult.GetValue(recipePath)!, cancellationToken);
            if (recipe is null)
            {
                return ExitCodes.Invalid;
            }

            if (recipe.ConfigMgr is not { } section)
            {
                Console.Error.WriteLine("The recipe has no configMgr section.");
                return ExitCodes.Invalid;
            }

            var problems = RecipeValidator.Validate(recipe, checkFiles: false).Where(e => e.StartsWith("configMgr", StringComparison.Ordinal)).ToList();
            foreach (var problem in problems)
            {
                Console.Error.WriteLine($"error: {problem}");
            }

            if (problems.Count > 0)
            {
                return ExitCodes.Invalid;
            }

            var progress = new ConsoleProgress();
            var path = parseResult.GetValue(wim)?.FullName ?? recipe.Output.Path;

            // {release} and {arch} come from the image itself, which takes the DISM API (and so elevation) to read.
            // Facts come from the image with DISM (so elevation): templates may use them, and updating an existing
            // package always needs the build for the downgrade check.
            ImageFacts? facts = null;
            var needsFacts = section.PackageId is not null
                || new[] { section.PackageName, section.Version, section.Description, section.SourceFolderName }.Any(PackageTemplate.NeedsImageFacts);
            if (needsFacts)
            {
                if (!Familiar.Core.ProcessElevation.IsElevated)
                {
                    Console.Error.WriteLine("Publishing reads the image's build and release with DISM (for templates and the downgrade check); run from an elevated prompt.");
                    return ExitCodes.NotElevated;
                }

                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"Nothing to publish: {path} does not exist.");
                    return ExitCodes.NotFound;
                }

                var catalog = await CatalogOptions.LoadAsync(parseResult, cancellationToken);
                using var dism = new Familiar.Core.Images.DismHost();
                facts = ImageFactsReader.Read(path, catalog);
                progress.Log($"Image: {facts.Release ?? "unknown release"} {facts.Edition} {facts.Architecture}, build {facts.Build}{(facts.Kb is null ? string.Empty : $" ({facts.Kb})")}");
            }

            try
            {
                var warnings = await BuildRunner.PublishAsync(recipe, section, path, facts, connect: null, progress, progress.Log, cancellationToken);
                foreach (var warning in warnings)
                {
                    progress.Log($"warning: {warning}");
                }

                return ExitCodes.Success;
            }
            catch (BuildException ex)
            {
                progress.Log($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }
}
