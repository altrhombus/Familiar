using System.CommandLine;
using Familiar.Core.Languages;
using Familiar.Core.Servicing;

namespace Familiar.Cli.Commands;

internal static class LanguagesCommand
{
    public static Command Create()
    {
        var media = new Argument<DirectoryInfo>("media")
        {
            Description = "Languages and Optional Features ISO root (mounted) or its LanguagesAndOptionalFeatures folder.",
        };

        var languages = new Option<string[]>("--lang")
        {
            Description = "Language to plan, e.g. de-DE. Repeat for several. Without it, lists what the media offers.",
            AllowMultipleArgumentsPerToken = true,
        };

        var features = new Option<LanguageFeature[]>("--feature")
        {
            Description = "Features to include (Basic is always added). Defaults to all.",
            AllowMultipleArgumentsPerToken = true,
        };

        var winPEPackages = new Option<string[]>("--winpe-package")
        {
            Description = "Also plan WinPE languages for an image containing these components (e.g. WinPE-Setup-Package).",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("languages", "Inspect language media and plan language servicing.")
        {
            media,
            languages,
            features,
            winPEPackages,
        };

        command.SetAction(parseResult =>
        {
            var directory = parseResult.GetValue(media)!;
            if (!directory.Exists)
            {
                Console.Error.WriteLine($"Language media not found: {directory.FullName}");
                return ExitCodes.NotFound;
            }

            var inventory = LanguageMedia.Scan(directory.FullName);
            var requested = parseResult.GetValue(languages) ?? [];

            if (requested.Length == 0)
            {
                PrintInventory(inventory);
                return ExitCodes.Success;
            }

            var selected = parseResult.GetValue(features) is { Length: > 0 } chosen
                ? new HashSet<LanguageFeature>(chosen)
                : new HashSet<LanguageFeature>(Enum.GetValues<LanguageFeature>());

            var plan = LanguagePlanner.PlanImage(new LanguageRequest { Languages = requested, Features = selected }, inventory);
            Print("Windows image", plan);

            if (parseResult.GetValue(winPEPackages) is { Length: > 0 } packages)
            {
                Console.WriteLine();
                var winPE = LanguagePlanner.PlanWinPE(requested, inventory, packages);
                Print("WinPE image", winPE);
                plan = plan with { Errors = [.. plan.Errors, .. winPE.Errors] };
            }

            return plan.IsValid ? ExitCodes.Success : ExitCodes.Invalid;
        });

        return command;
    }

    private static void PrintInventory(LanguageMedia media)
    {
        Console.WriteLine($"Features folder: {media.FeaturesPath} ({media.Architecture})");
        Console.WriteLine($"Language packs:  {media.LanguagePacks.Count} ({media.LanguagePacks.Values.Count(p => p.IsInterfacePack)} interface packs)");
        Console.WriteLine($"Languages:       {media.Features.Count} with language features");
        Console.WriteLine($"Fonts:           {string.Join(", ", media.Fonts.Keys.Order(StringComparer.Ordinal))}");
        Console.WriteLine($"WinPE languages: {media.WinPE.Count}");
        Console.WriteLine();
        Console.WriteLine($"{"Language",-14} {"Name",-36} {"Pack",-5} Features");

        foreach (var tag in media.Languages)
        {
            var pack = media.LanguagePacks.TryGetValue(tag, out var p) ? (p.IsInterfacePack ? "LIP" : "yes") : "-";
            var available = media.Features.TryGetValue(tag, out var f) ? string.Join(", ", f.Keys.Order()) : "-";
            var name = LanguageTag.DisplayName(tag);
            Console.WriteLine($"{tag,-14} {(name.Length > 36 ? name[..35] + "…" : name),-36} {pack,-5} {available}");
        }
    }

    private static void Print(string title, ServicingPlan plan)
    {
        Console.WriteLine($"{title}: {plan.Actions.Count} actions");
        var step = 1;
        foreach (var action in plan.Actions)
        {
            var detail = action switch
            {
                AddPackageAction package => Path.GetFileName(package.PackagePath),
                AddCapabilityAction capability => capability.CapabilityName,
                _ => string.Empty,
            };
            Console.WriteLine($"  {step++,3}. {action.Description,-40} {detail}");
        }

        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine($"  warning: {warning}");
        }

        foreach (var error in plan.Errors)
        {
            Console.WriteLine($"  error: {error}");
        }
    }
}
