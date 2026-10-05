using System.CommandLine;
using System.Text.Json;
using Familiar.Core.Languages;
using Familiar.Core.Recipes;
using Familiar.Core.Servicing;
using Familiar.Core.Updates;

namespace Familiar.Cli.Commands;

internal static class RecipeCommand
{
    public static Command Create()
    {
        var command = new Command("recipe", "Create and check build recipes.")
        {
            CreateNew(),
            CreateCheck(),
        };

        return command;
    }

    private static Command CreateNew()
    {
        var path = new Argument<FileInfo>("path") { Description = "Where to write the recipe." };
        var command = new Command("new", "Write a starter recipe to edit.") { path };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(path)!;
            if (file.Exists)
            {
                Console.Error.WriteLine($"{file.FullName} already exists.");
                return ExitCodes.Invalid;
            }

            await File.WriteAllTextAsync(file.FullName, RecipeSerializer.Serialize(RecipeSerializer.Template()), cancellationToken);
            Console.WriteLine($"Wrote {file.FullName}");
            return ExitCodes.Success;
        });

        return command;
    }

    private static Command CreateCheck()
    {
        var path = new Argument<FileInfo>("path") { Description = "Recipe to check." };
        var command = new Command("check", "Validate a recipe and its media without changing anything.")
        {
            path,
            CatalogOptions.Offline,
            CatalogOptions.Refresh,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var recipe = await LoadAsync(parseResult.GetValue(path)!, cancellationToken);
            if (recipe is null)
            {
                return ExitCodes.Invalid;
            }

            var errors = RecipeValidator.Validate(recipe).ToList();
            Console.WriteLine($"Recipe:  {recipe.Name ?? "(unnamed)"}");
            Console.WriteLine($"Source:  {recipe.Source.Path} index {recipe.Source.Index}");
            Console.WriteLine($"Output:  {recipe.Output.Path}");

            if (recipe.Languages is { } languages && Directory.Exists(languages.MediaPath))
            {
                var media = LanguageMedia.Scan(languages.MediaPath);
                var sample = media.LanguagePacks.Values.FirstOrDefault(p => !p.IsInterfacePack);
                var identity = sample is null ? null : await CabManifest.ReadIdentityAsync(sample.Path, cancellationToken);
                Console.WriteLine($"Media:   {media.FeaturesPath}, build {identity?.Version?.ToString() ?? "unknown"}");

                var plan = LanguagePlanner.PlanImage(new LanguageRequest { Languages = languages.Languages, IncludeFonts = languages.IncludeFonts }, media);
                Console.WriteLine($"Plan:    {plan.Actions.Count} language actions (before checking what the image already has)");
                foreach (var warning in plan.Warnings)
                {
                    Console.WriteLine($"warning: {warning}");
                }

                errors.AddRange(plan.Errors);
            }

            if (recipe.InboxApps is { } inboxApps && Directory.Exists(inboxApps.MediaPath))
            {
                var apps = Familiar.Core.InboxApps.InboxAppMedia.Scan(inboxApps.MediaPath);
                Console.WriteLine($"Apps:    {apps.PackagesPath}, {apps.Apps.Count} apps and {apps.Frameworks.Count} frameworks (re-provisions those already in the image)");
            }

            if (recipe.ConfigMgr is { } configMgr)
            {
                var target = configMgr.PackageId is { } id ? $"update {id}" : $"new package in {configMgr.PackageSourcePath}";
                Console.WriteLine($"Publish: {configMgr.SiteServer} ({configMgr.Transport}), {target}");
            }

            if (recipe.Updates is { } updates && Directory.Exists(updates.Folder))
            {
                var files = UpdatePlanner.Scan(updates.Folder);
                if (files.Count == 0)
                {
                    errors.Add($"No .msu or .cab files with a KB number were found in {updates.Folder}.");
                }
                else
                {
                    // The image's exact release is only known once it is mounted; describe each KB from the release data.
                    var catalog = await CatalogOptions.LoadAsync(parseResult, cancellationToken);
                    Console.WriteLine($"Updates: {updates.Folder}");
                    foreach (var file in files)
                    {
                        var known = catalog.Updates.Where(u => u.KbArticle.Equals(file.Kb, StringComparison.OrdinalIgnoreCase)).ToList();
                        var description = known.Count == 0
                            ? "not a Windows cumulative update in the release data (applied last; expected for .NET)"
                            : $"{string.Join("/", known.Select(u => u.WindowsVersion).Distinct())} {known[0].Type}, {known[0].ReleaseDate:yyyy-MM-dd}";
                        Console.WriteLine($"         {file.Kb} [{file.Architecture ?? "any"}] {description}");
                    }
                }
            }

            foreach (var error in errors)
            {
                Console.WriteLine($"error:   {error}");
            }

            Console.WriteLine(errors.Count == 0 ? "OK" : $"{errors.Count} problem(s)");
            return errors.Count == 0 ? ExitCodes.Success : ExitCodes.Invalid;
        });

        return command;
    }

    internal static async Task<BuildRecipe?> LoadAsync(FileInfo file, CancellationToken cancellationToken)
    {
        if (!file.Exists)
        {
            Console.Error.WriteLine($"Recipe not found: {file.FullName}");
            return null;
        }

        try
        {
            return await RecipeSerializer.LoadAsync(file.FullName, cancellationToken);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"{file.Name}: {ex.Message}");
            return null;
        }
    }
}
