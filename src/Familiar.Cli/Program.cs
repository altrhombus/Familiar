using System.CommandLine;
using Familiar.Core;
using Familiar.Core.Images;

var imageArgument = new Argument<FileInfo>("image")
{
    Description = "Path to a WIM or ESD file.",
};

var infoCommand = new Command("info", "List the indexes in a WIM or ESD file.") { imageArgument };
infoCommand.SetAction(parseResult =>
{
    var image = parseResult.GetValue(imageArgument)!;
    if (!image.Exists)
    {
        Console.Error.WriteLine($"Image not found: {image.FullName}");
        return 2;
    }

    if (!ProcessElevation.IsElevated)
    {
        Console.Error.WriteLine("Reading image metadata uses the DISM API, which requires an elevated prompt.");
        return 5;
    }

    using var dism = new DismSession();
    foreach (var index in dism.GetIndexes(image.FullName))
    {
        Console.WriteLine($"[{index.Index}] {index.Name}");
        Console.WriteLine($"    Release:  {index.Release?.Name ?? "unknown"} (build {index.Version})");
        Console.WriteLine($"    Edition:  {index.EditionId} / {index.InstallationType} / {index.Architecture}");
        Console.WriteLine($"    Language: {index.DefaultLanguage ?? "unknown"}");
    }

    return 0;
});

var releasesCommand = new Command("releases", "List the Windows releases Familiar recognizes.");
releasesCommand.SetAction(_ =>
{
    foreach (var release in WindowsReleaseCatalog.All)
    {
        Console.WriteLine($"{release.Name,-22} build {release.Build,-6} language media {release.LanguageMediaVersion}");
    }

    return 0;
});

var rootCommand = new RootCommand("Familiar: offline Windows image servicing.")
{
    infoCommand,
    releasesCommand,
};

return rootCommand.Parse(args).Invoke();
