using System.CommandLine;
using Familiar.Core;
using Familiar.Core.Images;
using Familiar.Core.Recipes;
using Familiar.Core.Servicing;

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

        var command = new Command("build", "Build an image from a recipe.") { path, logFile };

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

            var progress = new ConsoleProgress();

            try
            {
                using var dism = new DismHost(parseResult.GetValue(logFile)?.FullName);
                var result = await new BuildRunner().RunAsync(recipe, progress, progress.Log, cancellationToken);
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

    /// <summary>Rewrites one status line per operation; log messages go above it.</summary>
    private sealed class ConsoleProgress : IProgress<ServicingProgress>
    {
        private readonly Lock _gate = new();
        private bool _onStatusLine;

        public void Report(ServicingProgress value)
        {
            if (Console.IsOutputRedirected)
            {
                return;
            }

            lock (_gate)
            {
                var percent = value.Percent is { } p ? $" {p,5:0.0}%" : string.Empty;
                var text = $"  {value.Operation}{percent}";
                var width = Math.Max(1, Console.WindowWidth - 1);
                Console.Write("\r" + (text.Length > width ? text[..width] : text.PadRight(width)));
                _onStatusLine = true;
            }
        }

        public void Log(string message)
        {
            lock (_gate)
            {
                if (_onStatusLine)
                {
                    Console.Write("\r" + new string(' ', Math.Max(1, Console.WindowWidth - 1)) + "\r");
                    _onStatusLine = false;
                }

                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
            }
        }
    }
}
