using System.Text.Json;
using System.Text.Json.Serialization;
using Familiar.Core.Languages;

namespace Familiar.Core.Recipes;

public static class RecipeSerializer
{
    public static async Task<BuildRecipe> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var recipe = await JsonSerializer.DeserializeAsync(stream, RecipeJsonContext.Default.BuildRecipe, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("The recipe is empty.");
        return Resolve(recipe, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
    }

    public static BuildRecipe Parse(string json, string baseDirectory)
    {
        var recipe = JsonSerializer.Deserialize(json, RecipeJsonContext.Default.BuildRecipe)
            ?? throw new JsonException("The recipe is empty.");
        return Resolve(recipe, baseDirectory);
    }

    public static string Serialize(BuildRecipe recipe) =>
        JsonSerializer.Serialize(recipe, RecipeJsonContext.Default.BuildRecipe);

    public static BuildRecipe Template() => new()
    {
        Name = "Windows 11 Enterprise, German and Japanese",
        Source = new SourceSection { Path = @"E:\sources\install.wim", Index = 3 },
        WorkingDirectory = @"C:\FamiliarWork",
        Languages = new LanguagesSection
        {
            MediaPath = @"F:\",
            Languages = ["de-DE", "ja-JP"],
            DefaultLanguage = "de-DE",
        },
        Updates = new UpdatesSection { Folder = @"C:\Updates\Windows11-25H2" },
        Output = new OutputSection { Path = @"D:\Familiar\Output\install-de-ja.wim" },
    };

    // Relative paths are relative to the recipe file so recipes can travel with their media.
    private static BuildRecipe Resolve(BuildRecipe recipe, string baseDirectory)
    {
        string Full(string path) => System.IO.Path.GetFullPath(path, baseDirectory);

        return recipe with
        {
            Source = recipe.Source with { Path = Full(recipe.Source.Path) },
            WorkingDirectory = Full(recipe.WorkingDirectory),
            Languages = recipe.Languages is null ? null : recipe.Languages with { MediaPath = Full(recipe.Languages.MediaPath) },
            InboxApps = recipe.InboxApps is null ? null : recipe.InboxApps with { MediaPath = Full(recipe.InboxApps.MediaPath) },
            Updates = recipe.Updates is null ? null : recipe.Updates with { Folder = Full(recipe.Updates.Folder) },
            Output = recipe.Output with { Path = Full(recipe.Output.Path) },
        };
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BuildRecipe))]
internal sealed partial class RecipeJsonContext : JsonSerializerContext;
