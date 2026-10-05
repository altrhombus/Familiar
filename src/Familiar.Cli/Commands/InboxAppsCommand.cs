using System.CommandLine;
using Familiar.Core.InboxApps;

namespace Familiar.Cli.Commands;

internal static class InboxAppsCommand
{
    public static Command Create()
    {
        var media = new Argument<DirectoryInfo>("media") { Description = "Inbox Apps ISO root (mounted) or its packages folder." };
        var architecture = new Option<string>("--arch") { Description = "x64 or arm64.", DefaultValueFactory = _ => "x64" };
        architecture.AcceptOnlyFromAmong("x64", "arm64");

        var command = new Command("inbox-apps", "Inspect Inbox Apps media and the dependencies each app would be provisioned with.")
        {
            media,
            architecture,
        };

        command.SetAction(parseResult =>
        {
            var directory = parseResult.GetValue(media)!;
            if (!directory.Exists)
            {
                Console.Error.WriteLine($"Inbox apps media not found: {directory.FullName}");
                return ExitCodes.NotFound;
            }

            var arch = parseResult.GetValue(architecture)!;
            var inventory = InboxAppMedia.Scan(directory.FullName);
            Console.WriteLine($"Packages:   {inventory.PackagesPath}");
            Console.WriteLine($"Apps:       {inventory.Apps.Count}");
            Console.WriteLine($"Frameworks: {inventory.Frameworks.Count}");
            foreach (var skipped in inventory.Skipped)
            {
                Console.WriteLine($"Skipped:    {skipped}");
            }

            // Without an image, plan as if every app on the media were provisioned at version 0.
            var everything = inventory.Apps.Select(a => new ProvisionedApp(a.Identity.Name, new Version(0, 0)));
            var plan = InboxAppPlanner.Plan(inventory, everything, arch);

            Console.WriteLine();
            foreach (var action in plan.Actions.OfType<AddProvisionedAppAction>())
            {
                var dependencies = action.DependencyPaths.Count == 0 ? "no dependencies" : string.Join(", ", action.DependencyPaths.Select(Path.GetFileName));
                Console.WriteLine($"{action.Description,-70} {dependencies}");
            }

            foreach (var warning in plan.Warnings)
            {
                Console.WriteLine($"warning: {warning}");
            }

            return ExitCodes.Success;
        });

        return command;
    }
}
