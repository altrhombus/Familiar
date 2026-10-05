using System.CommandLine;
using Familiar.Cli.Commands;

var rootCommand = new RootCommand("Familiar: offline Windows image servicing.")
{
    InfoCommand.Create(),
    ReleasesCommand.Create(),
    LanguagesCommand.Create(),
};

return await rootCommand.Parse(args).InvokeAsync();
