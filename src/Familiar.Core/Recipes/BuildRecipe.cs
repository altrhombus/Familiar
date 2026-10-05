using Familiar.Core.Languages;

namespace Familiar.Core.Recipes;

/// <summary>
/// A saved build: which image to start from and what to do to it. Paths may be relative to the recipe file.
/// </summary>
/// <remarks>
/// Properties with defaults use <c>set</c> rather than <c>init</c>: the System.Text.Json source generator
/// assigns every init-only property, so a value missing from the JSON would overwrite its default with
/// <c>default(T)</c>.
/// </remarks>
public sealed record BuildRecipe
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? Name { get; init; }

    public required SourceSection Source { get; init; }

    /// <summary>Scratch space for the working copy and mount folders. Needs roughly three times the WIM size.</summary>
    public string WorkingDirectory { get; set; } = @"C:\FamiliarWork";

    public LanguagesSection? Languages { get; init; }

    public required OutputSection Output { get; init; }
}

public sealed record SourceSection
{
    /// <summary>install.wim or install.esd.</summary>
    public required string Path { get; init; }

    public int Index { get; set; } = 1;
}

public sealed record LanguagesSection
{
    /// <summary>Languages and Optional Features ISO root (mounted) or its LanguagesAndOptionalFeatures folder.</summary>
    public required string MediaPath { get; init; }

    public required IReadOnlyList<string> Languages { get; init; }

    /// <summary>Features per language; Basic is always added. Omit for all.</summary>
    public IReadOnlyList<LanguageFeature>? Features { get; init; }

    public bool IncludeFonts { get; set; } = true;

    /// <summary>UI language, locale and input defaults. Must be one of <see cref="Languages"/>.</summary>
    public string? DefaultLanguage { get; init; }

    /// <summary>Also localize Windows Recovery (WinRE) inside the image.</summary>
    public bool UpdateWinRE { get; set; } = true;
}

public sealed record OutputSection
{
    /// <summary>Destination WIM. Never the source file.</summary>
    public required string Path { get; init; }

    /// <summary>Image name stored in the WIM; defaults to the source index's name.</summary>
    public string? ImageName { get; init; }

    /// <summary>Remove superseded components before saving. Makes the image smaller; updates stay uninstallable.</summary>
    public bool CleanupComponents { get; set; } = true;
}
