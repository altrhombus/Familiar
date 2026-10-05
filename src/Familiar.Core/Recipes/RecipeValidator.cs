namespace Familiar.Core.Recipes;

public static class RecipeValidator
{
    /// <summary>Checks a recipe without touching DISM. Returns problems; empty means it can run.</summary>
    /// <param name="checkFiles">Also require the source image and language media to exist.</param>
    public static IReadOnlyList<string> Validate(BuildRecipe recipe, bool checkFiles = true)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var errors = new List<string>();

        if (recipe.SchemaVersion != BuildRecipe.CurrentSchemaVersion)
        {
            errors.Add($"schemaVersion {recipe.SchemaVersion} is not supported; this Familiar reads version {BuildRecipe.CurrentSchemaVersion}.");
        }

        if (recipe.Source.Index < 1)
        {
            errors.Add("source.index must be 1 or greater.");
        }

        var extension = Path.GetExtension(recipe.Source.Path);
        if (!extension.Equals(".wim", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".esd", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("source.path must be a .wim or .esd file.");
        }

        if (!Path.GetExtension(recipe.Output.Path).Equals(".wim", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("output.path must be a .wim file.");
        }

        if (string.Equals(Path.GetFullPath(recipe.Source.Path), Path.GetFullPath(recipe.Output.Path), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("output.path must differ from source.path; Familiar never modifies the source image.");
        }

        if (IsInside(recipe.Output.Path, recipe.WorkingDirectory))
        {
            errors.Add("output.path must not be inside workingDirectory, which holds temporary build files.");
        }

        if (recipe.Languages is { } languages)
        {
            if (languages.Languages.Count == 0)
            {
                errors.Add("languages.languages must list at least one language.");
            }

            if (languages.DefaultLanguage is { } fallback
                && !languages.Languages.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"languages.defaultLanguage {fallback} must be one of languages.languages.");
            }

            if (checkFiles && !Directory.Exists(languages.MediaPath))
            {
                errors.Add($"Language media not found: {languages.MediaPath}");
            }
        }

        if (checkFiles && recipe.Updates is { Download: false } updates && !Directory.Exists(updates.Folder))
        {
            errors.Add($"Updates folder not found: {updates.Folder}");
        }

        if (checkFiles && !File.Exists(recipe.Source.Path))
        {
            errors.Add($"Source image not found: {recipe.Source.Path}");
        }

        return errors;
    }

    private static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
