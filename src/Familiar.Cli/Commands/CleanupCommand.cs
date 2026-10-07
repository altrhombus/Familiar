using System.CommandLine;
using Familiar.Core;
using Familiar.Core.Images;

namespace Familiar.Cli.Commands;

internal static class CleanupCommand
{
    public static Command Create()
    {
        var workingDirectory = new Option<DirectoryInfo>("--working-directory")
        {
            Description = "Working directory used by builds (the recipe's workingDirectory).",
            DefaultValueFactory = _ => new DirectoryInfo(@"C:\FamiliarWork"),
        };

        var command = new Command(
            "cleanup",
            "Discard images left mounted by an interrupted build and remove its working folders. Only folders Familiar created are touched.")
        {
            workingDirectory,
        };

        command.SetAction(parseResult =>
        {
            if (!ProcessElevation.IsElevated)
            {
                Console.Error.WriteLine("Cleaning up mounts uses the DISM API, which requires an elevated prompt.");
                return ExitCodes.NotElevated;
            }

            var root = parseResult.GetValue(workingDirectory)!.FullName;
            try
            {
                using var dism = new DismHost();
                var result = WorkingFolders.Clean(root, new DismMountTable(), Console.WriteLine);
                foreach (var problem in result.Problems)
                {
                    Console.Error.WriteLine($"error: {problem}");
                }

                Console.WriteLine(result.Discarded.Count == 0 && result.Removed.Count == 0
                    ? $"Nothing of Familiar's to clean up in {root}."
                    : $"Discarded {result.Discarded.Count} mount(s), removed {result.Removed.Count} working folder(s).");
                return result.Problems.Count == 0 ? ExitCodes.Success : ExitCodes.Failure;
            }
            catch (Exception ex) when (DismErrors.Describe(ex) is { } description)
            {
                Console.Error.WriteLine($"error: {description}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }
}
