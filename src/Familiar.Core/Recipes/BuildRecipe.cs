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

    public InboxAppsSection? InboxApps { get; init; }

    public UpdatesSection? Updates { get; init; }

    public required OutputSection Output { get; init; }

    /// <summary>Publish the output to Configuration Manager as an OS image package after the build.</summary>
    public ConfigMgrSection? ConfigMgr { get; init; }
}

public sealed record ConfigMgrSection
{
    /// <summary>FQDN of the server hosting the SMS Provider (and the administration service).</summary>
    public required string SiteServer { get; init; }

    /// <summary>Usually discovered from the provider; set it in hierarchies with more than one site.</summary>
    public string? SiteCode { get; init; }

    public ConfigMgr.ConfigMgrTransport Transport { get; set; } = ConfigMgr.ConfigMgrTransport.Auto;

    /// <summary>
    /// Existing OS image package to update. Omit to create a new one. Task sequences using this package deploy
    /// whatever its source holds, so point recipes at a test package first; the replaced WIM is kept as
    /// <c>.previous</c> for <c>familiar configmgr rollback</c>.
    /// </summary>
    public string? PackageId { get; init; }

    /// <summary>
    /// For an existing package, push the new image to its distribution points immediately (default). Set false to
    /// update the source only and release it from the console once tested.
    /// </summary>
    public bool RefreshDistributionPoints { get; set; } = true;

    /// <summary>UNC folder the WIM is copied to for a new package, e.g. \\cm01\Sources$\OSD\Win11-25H2.</summary>
    public string? PackageSourcePath { get; init; }

    /// <summary>
    /// Give every publish its own folder, named from a template such as <c>{release}_{arch}_{date:yyMM}_{n}</c>
    /// (25H2_AMD64_2610_1). Existing packages are repointed at the new folder and the old one is left as it was,
    /// which is what rollback returns to. Omit to copy into packageSourcePath and update packages in place.
    /// </summary>
    public string? SourceFolderName { get; init; }

    // Name, version and description accept the tokens in ConfigMgr.PackageTemplate.TokenHelp, e.g.
    // "Windows 11 {arch}", "{release}" or "{cu:yyyy-MM}". Existing packages change only the fields that are set.

    /// <summary>Package name. New packages default to the recipe name; existing packages are renamed only when set.</summary>
    public string? PackageName { get; init; }

    /// <summary>Package version. New packages default to the build date (yyyy.MM.dd); existing ones keep theirs unless set.</summary>
    public string? Version { get; init; }

    /// <summary>Package description (the console's comment). Changed only when set.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Allow publishing an image whose build is older than the one the existing package already has. Off by default,
    /// so a skipped update or older media cannot roll deployments back.
    /// </summary>
    public bool AllowOlderImage { get; set; }

    public IReadOnlyList<string> DistributionPointGroups { get; set; } = [];

    /// <summary>Distribution point server names (FQDN or short name).</summary>
    public IReadOnlyList<string> DistributionPoints { get; set; } = [];
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

public sealed record InboxAppsSection
{
    /// <summary>
    /// Inbox Apps ISO root (mounted) or its packages folder. Apps already in the image are re-provisioned from it
    /// after languages are added, so they pick up resources for the new languages. Removed apps stay removed.
    /// </summary>
    public required string MediaPath { get; init; }
}

public sealed record UpdatesSection
{
    /// <summary>
    /// Folder of .msu/.cab files, applied after languages. For 24H2 and later, download every file the Microsoft
    /// Update Catalog lists for the cumulative update; checkpoint updates are applied first automatically.
    /// </summary>
    public required string Folder { get; init; }

    /// <summary>
    /// Before building, download the latest cumulative update (with its checkpoints) for the source image's
    /// release from the Microsoft Update Catalog into <see cref="Folder"/>. Files already there are reused.
    /// </summary>
    public bool Download { get; set; }

    /// <summary>When downloading, also fetch the latest .NET Framework cumulative update.</summary>
    public bool IncludeDotNet { get; set; } = true;
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
