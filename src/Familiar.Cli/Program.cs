using System.CommandLine;
using Familiar.Cli;
using Familiar.Cli.Commands;

var rootCommand = new RootCommand("Familiar: offline Windows image servicing.")
{
    InfoCommand.Create(),
    ReleasesCommand.Create(),
    LanguagesCommand.Create(),
    RecipeCommand.Create(),
    BuildCommand.Create(),
    CleanupCommand.Create(),
    UpdatesCommand.Create(),
    InboxAppsCommand.Create(),
    ConfigMgrCommand.Create(),
    DoctorCommand.Create(),
};

// Familiar handles Ctrl+C itself (see Interruption); a null timeout turns System.CommandLine's handling off.
using var interruption = new Interruption(Console.Error);
Console.CancelKeyPress += (_, e) => e.Cancel = interruption.OnInterrupt();

return await rootCommand.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null }, interruption.Token);
