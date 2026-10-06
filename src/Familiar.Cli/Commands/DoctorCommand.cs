using System.CommandLine;
using Familiar.Core.Diagnostics;
using Familiar.Core.Recipes;

namespace Familiar.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Create()
    {
        var recipePath = new Option<FileInfo?>("--recipe")
        {
            Description = "Also check this recipe's media, updates, working directory and ConfigMgr access.",
        };

        var command = new Command("doctor", "Check that this machine is ready to build images.") { recipePath };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            BuildRecipe? recipe = null;
            if (parseResult.GetValue(recipePath) is { } file)
            {
                recipe = await RecipeCommand.LoadAsync(file, cancellationToken);
                if (recipe is null)
                {
                    return ExitCodes.Invalid;
                }
            }

            using var http = Http.Create();
            var results = await new BuildMachineDoctor(http).RunAsync(recipe, cancellationToken);

            foreach (var area in results.GroupBy(r => r.Area))
            {
                Console.WriteLine(area.Key);
                foreach (var result in area)
                {
                    var mark = result.Status switch
                    {
                        CheckStatus.Pass => "ok  ",
                        CheckStatus.Info => "info",
                        CheckStatus.Warn => "WARN",
                        _ => "FAIL",
                    };
                    Console.WriteLine($"  [{mark}] {result.Name}: {result.Detail}");
                    if (result.Fix is not null && result.Status is CheckStatus.Warn or CheckStatus.Fail)
                    {
                        Console.WriteLine($"         -> {result.Fix}");
                    }
                }
            }

            var failures = results.Count(r => r.Status == CheckStatus.Fail);
            var warnings = results.Count(r => r.Status == CheckStatus.Warn);
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? $"Ready ({warnings} warning(s))." : $"{failures} problem(s) to fix, {warnings} warning(s).");
            return failures == 0 ? ExitCodes.Success : ExitCodes.Failure;
        });

        return command;
    }
}
