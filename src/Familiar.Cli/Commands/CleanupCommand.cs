using System.CommandLine;
using Familiar.Core;
using Familiar.Core.Images;
using Microsoft.Dism;

namespace Familiar.Cli.Commands;

internal static class CleanupCommand
{
    public static Command Create()
    {
        var workingDirectory = new Option<DirectoryInfo>("--working-directory")
        {
            Description = "Working directory used by builds.",
            DefaultValueFactory = _ => new DirectoryInfo(@"C:\FamiliarWork"),
        };

        var command = new Command("cleanup", "Discard images left mounted by an interrupted build and remove its working files.")
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

            var root = Path.TrimEndingDirectorySeparator(parseResult.GetValue(workingDirectory)!.FullName) + Path.DirectorySeparatorChar;

            try
            {
                using var dism = new DismHost();

                // Only touch mounts Familiar made; other tools' mounts are none of our business.
                var ours = DismApi.GetMountedImages()
                    .Where(m => m.MountPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => m.MountPath.Length) // WinRE mounts sit beside, not inside, but unmount deepest first anyway.
                    .ToList();

                foreach (var mount in ours)
                {
                    Console.WriteLine($"Discarding {mount.MountPath} ({mount.ImageFilePath}, {mount.MountStatus})");
                    DismApi.UnmountImage(mount.MountPath, commitChanges: false);
                }

                DismApi.CleanupMountpoints();
                Console.WriteLine(ours.Count == 0 ? "No Familiar mounts found." : $"Discarded {ours.Count} mount(s).");
            }
            catch (Exception ex) when (DismErrors.Describe(ex) is { } description)
            {
                Console.Error.WriteLine($"error: {description}");
                return ExitCodes.Failure;
            }

            if (Directory.Exists(root))
            {
                foreach (var build in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        Directory.Delete(build, recursive: true);
                        Console.WriteLine($"Removed {build}");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"Could not remove {build}: {ex.Message}");
                    }
                }
            }

            return ExitCodes.Success;
        });

        return command;
    }
}
