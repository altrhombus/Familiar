using System.Text.RegularExpressions;

namespace Familiar.Core.Languages;

/// <summary>The per-language Features on Demand that make up a full language experience.</summary>
public enum LanguageFeature
{
    Basic,
    Handwriting,
    OCR,
    Speech,
    TextToSpeech,
}

/// <param name="IsInterfacePack">
/// A Language Interface Pack (LIP) only partially translates Windows and needs a fully localized parent language.
/// </param>
public sealed record LanguagePackFile(string Tag, string Path, bool IsInterfacePack);

/// <summary>WinPE language resources for one language: <c>lp.cab</c> plus satellites for optional components.</summary>
/// <param name="Components">Optional component name (e.g. "WinPE-Setup") to its language cab.</param>
public sealed record WinPELanguage(string Tag, string LanguagePackPath, IReadOnlyDictionary<string, string> Components);

/// <summary>
/// Inventory of a Languages and Optional Features ISO (or a copy of it), built from file names so that new
/// languages and features on the media show up without code changes.
/// </summary>
public sealed partial class LanguageMedia
{
    private const string FeaturesFolder = "LanguagesAndOptionalFeatures";
    private const string WinPEFolder = "Windows Preinstallation Environment";

    private LanguageMedia(string featuresPath, string architecture)
    {
        FeaturesPath = featuresPath;
        Architecture = architecture;
    }

    /// <summary>Folder holding the FOD and language cabs; pass it to DISM as the capability source.</summary>
    public string FeaturesPath { get; }

    /// <summary>Package architecture in servicing terms: "amd64" or "arm64".</summary>
    public string Architecture { get; }

    public IReadOnlyDictionary<string, LanguagePackFile> LanguagePacks { get; private set; } = Empty<LanguagePackFile>();

    public IReadOnlyDictionary<string, IReadOnlyDictionary<LanguageFeature, string>> Features { get; private set; } =
        Empty<IReadOnlyDictionary<LanguageFeature, string>>();

    /// <summary>Script code (e.g. "Jpan", "Hans") to its font package.</summary>
    public IReadOnlyDictionary<string, string> Fonts { get; private set; } = Empty<string>();

    public IReadOnlyDictionary<string, WinPELanguage> WinPE { get; private set; } = Empty<WinPELanguage>();

    /// <summary>WinPE font support cabs keyed by language tag (e.g. ja-JP); needed for East Asian setup UI.</summary>
    public IReadOnlyDictionary<string, string> WinPEFontSupport { get; private set; } = Empty<string>();

    /// <summary>Every language tag with a language pack or a Basic feature on the media.</summary>
    public IEnumerable<string> Languages => LanguagePacks.Keys.Union(Features.Keys, StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scans an ISO root, its LanguagesAndOptionalFeatures folder, or a flat folder of cabs.</summary>
    /// <param name="path">Media root or features folder.</param>
    /// <param name="architecture">"amd64" or "arm64"; selects matching packages when the media holds both.</param>
    public static LanguageMedia Scan(string path, string architecture = "amd64")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Language media not found: {path}");
        }

        var nested = System.IO.Path.Combine(path, FeaturesFolder);
        var featuresPath = Directory.Exists(nested) ? nested : path;
        var mediaRoot = Directory.Exists(nested) ? path : Directory.GetParent(path)?.FullName;

        var media = new LanguageMedia(featuresPath, architecture.ToLowerInvariant());
        media.ScanFeatures(Directory.EnumerateFiles(featuresPath, "*.cab"));

        var winPE = mediaRoot is null ? null : FindWinPEComponents(System.IO.Path.Combine(mediaRoot, WinPEFolder), media.Architecture);
        if (winPE is not null)
        {
            media.ScanWinPE(winPE);
        }

        return media;
    }

    /// <summary>Scans WinPE optional components from the ADK or media (the folder containing per-language subfolders).</summary>
    public LanguageMedia WithWinPE(string winPEComponentsPath)
    {
        ScanWinPE(winPEComponentsPath);
        return this;
    }

    internal void ScanFeatures(IEnumerable<string> files)
    {
        var packs = new Dictionary<string, LanguagePackFile>(StringComparer.OrdinalIgnoreCase);
        var features = new Dictionary<string, Dictionary<LanguageFeature, string>>(StringComparer.OrdinalIgnoreCase);
        var fonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packArchitecture = Architecture == "amd64" ? "x64" : Architecture;

        foreach (var file in files)
        {
            var name = System.IO.Path.GetFileName(file);

            var pack = LanguagePackPattern().Match(name);
            if (pack.Success && pack.Groups["arch"].Value.Equals(packArchitecture, StringComparison.OrdinalIgnoreCase))
            {
                var tag = LanguageTag.Normalize(pack.Groups["tag"].Value);
                packs[tag] = new LanguagePackFile(tag, file, pack.Groups["kind"].Value.Equals("Lip", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            var feature = LanguageFeaturePattern().Match(name);
            if (feature.Success && IsArchitecture(feature, Architecture) && Enum.TryParse<LanguageFeature>(feature.Groups["kind"].Value, ignoreCase: true, out var kind))
            {
                var tag = LanguageTag.Normalize(feature.Groups["tag"].Value);
                if (!features.TryGetValue(tag, out var byKind))
                {
                    features[tag] = byKind = [];
                }

                byKind[kind] = file;
                continue;
            }

            var font = FontPattern().Match(name);
            if (font.Success && IsArchitecture(font, Architecture))
            {
                fonts[font.Groups["script"].Value] = file;
            }
        }

        LanguagePacks = packs;
        Features = features.ToDictionary(
            p => p.Key, IReadOnlyDictionary<LanguageFeature, string> (p) => p.Value, StringComparer.OrdinalIgnoreCase);
        Fonts = fonts;
    }

    private void ScanWinPE(string componentsPath)
    {
        var languages = new Dictionary<string, WinPELanguage>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(componentsPath))
        {
            var lp = System.IO.Path.Combine(directory, "lp.cab");
            if (!File.Exists(lp))
            {
                continue;
            }

            var tag = LanguageTag.Normalize(System.IO.Path.GetFileName(directory));
            var suffix = "_" + System.IO.Path.GetFileName(directory);
            var components = Directory.EnumerateFiles(directory, "*.cab")
                .Select(f => (Name: System.IO.Path.GetFileNameWithoutExtension(f), Path: f))
                .Where(f => f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Name[..^suffix.Length], f => f.Path, StringComparer.OrdinalIgnoreCase);

            languages[tag] = new WinPELanguage(tag, lp, components);
        }

        WinPE = languages;
        WinPEFontSupport = Directory.EnumerateFiles(componentsPath, "WinPE-FontSupport-*.cab")
            .ToDictionary(
                f => LanguageTag.Normalize(System.IO.Path.GetFileNameWithoutExtension(f)["WinPE-FontSupport-".Length..]),
                f => f,
                StringComparer.OrdinalIgnoreCase);
    }

    // Media uses "x64"; the ADK uses "amd64".
    private static string? FindWinPEComponents(string winPERoot, string architecture)
    {
        foreach (var folder in architecture == "amd64" ? new[] { "x64", "amd64" } : [architecture])
        {
            var candidate = System.IO.Path.Combine(winPERoot, folder, "WinPE_OCs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsArchitecture(Match match, string architecture) =>
        match.Groups["arch"].Value.Equals(architecture, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, T> Empty<T>() => new(StringComparer.OrdinalIgnoreCase);

    // Microsoft-Windows-Client-Language-Pack_x64_de-de.cab, Microsoft-Windows-Lip-Language-Pack_x64_ca-es.cab
    [GeneratedRegex(@"^Microsoft-Windows-(?<kind>Client|Lip)-Language-Pack_(?<arch>[a-z0-9]+)_(?<tag>[a-z0-9-]+)\.cab$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguagePackPattern();

    // Microsoft-Windows-LanguageFeatures-Basic-de-de-Package~31bf3856ad364e35~amd64~~.cab
    [GeneratedRegex(@"^Microsoft-Windows-LanguageFeatures-(?<kind>Basic|Handwriting|OCR|Speech|TextToSpeech)-(?<tag>[a-z0-9-]+)-Package~[0-9a-f]+~(?<arch>[a-z0-9]+)~~\.cab$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageFeaturePattern();

    // Microsoft-Windows-LanguageFeatures-Fonts-Jpan-Package~31bf3856ad364e35~amd64~~.cab
    [GeneratedRegex(@"^Microsoft-Windows-LanguageFeatures-Fonts-(?<script>[A-Za-z]+)-Package~[0-9a-f]+~(?<arch>[a-z0-9]+)~~\.cab$", RegexOptions.IgnoreCase)]
    private static partial Regex FontPattern();
}
