using System.CommandLine;
using Familiar.Core;
using Familiar.Core.Images;
using Familiar.Core.Recipes;
using Familiar.Core.Updates;

namespace Familiar.Cli.Commands;

internal static class BuildCommand
{
    public static Command Create()
    {
        var path = new Argument<FileInfo>("recipe") { Description = "Recipe to build." };
        var logFile = new Option<FileInfo?>("--dism-log")
        {
            Description = "Write the DISM API log here (useful when a package fails to apply).",
        };

        var noPublish = new Option<bool>("--no-publish")
        {
            Description = "Skip the recipe's configMgr section; publish later with 'familiar configmgr publish <recipe>'.",
        };

        var command = new Command("build", "Build an image from a recipe.")
        {
            path,
            logFile,
            noPublish,
            CatalogOptions.Offline,
            CatalogOptions.Refresh,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (!ProcessElevation.IsElevated)
            {
                Console.Error.WriteLine("Building images uses the DISM API, which requires an elevated prompt.");
                return ExitCodes.NotElevated;
            }

            var recipe = await RecipeCommand.LoadAsync(parseResult.GetValue(path)!, cancellationToken);
            if (recipe is null)
            {
                return ExitCodes.Invalid;
            }

            if (parseResult.GetValue(noPublish) && recipe.ConfigMgr is not null)
            {
                Console.WriteLine("Not publishing to Configuration Manager (--no-publish).");
                recipe = recipe with { ConfigMgr = null };
            }

            var catalog = await CatalogOptions.LoadAsync(parseResult, cancellationToken);
            using var http = Http.Create();
            var downloader = parseResult.GetValue(CatalogOptions.Offline) ? null : new UpdateDownloader(http, catalog);
            var progress = new ConsoleProgress();

            try
            {
                using var dism = new DismHost(parseResult.GetValue(logFile)?.FullName);
                var result = await new BuildRunner(catalog, downloader).RunAsync(recipe, progress, progress.Log, cancellationToken);
                foreach (var warning in result.Warnings)
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
            catch (OperationCanceledException)
            {
                progress.Log("Cancelled; changes were discarded.");
                return ExitCodes.Failure;
            }
            catch (Exception ex) when (DismErrors.Describe(ex) is { } description)
            {
                progress.Log($"error: {description}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }
}
