using Familiar.Core.Servicing;

namespace Familiar.Core.Languages;

public sealed record LanguageRequest
{
    /// <summary>Languages to add, as BCP 47 tags (e.g. "de-DE").</summary>
    public required IReadOnlyList<string> Languages { get; init; }

    /// <summary>Features to add for each language. Basic is always added.</summary>
    public IReadOnlySet<LanguageFeature> Features { get; init; } = new HashSet<LanguageFeature>(Enum.GetValues<LanguageFeature>());

    /// <summary>Add the font capability a language's script needs (e.g. Jpan for Japanese).</summary>
    public bool IncludeFonts { get; init; } = true;
}

/// <summary>What is already in the target image, so planning can skip it and use exact capability names.</summary>
/// <param name="Capabilities">Capability name to whether it is installed, from DISM GetCapabilities.</param>
/// <param name="InstalledLanguagePacks">Language tags whose language pack is installed.</param>
public sealed record ImageLanguageState(
    IReadOnlyDictionary<string, bool> Capabilities,
    IReadOnlySet<string> InstalledLanguagePacks);

public static class LanguagePlanner
{
    // Microsoft's documented order for language Features on Demand.
    private static readonly LanguageFeature[] FeatureOrder =
    [
        LanguageFeature.Basic,
        LanguageFeature.OCR,
        LanguageFeature.Handwriting,
        LanguageFeature.TextToSpeech,
        LanguageFeature.Speech,
    ];

    // East Asian setup UI needs WinPE font support in boot.wim and WinRE.
    private static readonly string[] WinPEFontLanguages = ["ja-JP", "ko-KR", "zh-CN", "zh-TW", "zh-HK"];

    /// <summary>Plans language packs and language features for a Windows image.</summary>
    /// <param name="state">Current image contents; when null, capability names use the standard 0.0.1.0 form.</param>
    public static ServicingPlan PlanImage(LanguageRequest request, LanguageMedia media, ImageLanguageState? state = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(media);

        var packs = new List<ServicingAction>();
        var features = new List<ServicingAction>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var plannedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requested in request.Languages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tag = LanguageTag.Normalize(requested);
            var hasPack = media.LanguagePacks.TryGetValue(tag, out var pack);
            var hasFeatures = media.Features.TryGetValue(tag, out var available);

            if (!hasPack && !hasFeatures)
            {
                errors.Add($"{tag} is not on the language media.");
                continue;
            }

            if (hasPack && state?.InstalledLanguagePacks.Contains(tag) != true)
            {
                packs.Add(new AddPackageAction(pack!.Path, $"Language pack {tag} ({LanguageTag.DisplayName(tag)})"));
                if (pack.IsInterfacePack)
                {
                    warnings.Add($"{tag} is a Language Interface Pack; it only partially translates Windows and needs its parent language in the image.");
                }
            }
            else if (!hasPack)
            {
                // Regional variants (de-CH, en-CA, es-US) display with their parent language's pack. On Windows 11,
                // .appx Local Experience Packs cannot be added to an image, so with no parent the UI stays as is.
                // Which same-language pack Windows uses for display is Windows' choice, so name the candidates only.
                var parents = FindParentLanguages(tag, request.Languages, media, state);
                warnings.Add(parents.Count > 0
                    ? $"{tag} has no language pack of its own; it adds its regional typing, speech and handwriting features and displays using the image's {LanguageTag.DisplayName(tag.Split('-')[0])} language pack ({string.Join(", ", parents)})."
                    : $"{tag} has no language pack on the media, and no language pack for the same language is in the image or the request; only its typing and speech features are added. Users can add it as a display language from Settings.");
            }

            foreach (var feature in FeatureOrder)
            {
                if (feature != LanguageFeature.Basic && !request.Features.Contains(feature))
                {
                    continue;
                }

                if (available?.ContainsKey(feature) != true)
                {
                    if (feature == LanguageFeature.Basic)
                    {
                        errors.Add($"{tag} has no Basic language feature on the media.");
                    }

                    continue;
                }

                AddCapability(features, $"Language.{feature}~~~{tag}~", $"{feature} for {tag}", state, media.FeaturesPath);

                if (feature == LanguageFeature.Basic && request.IncludeFonts)
                {
                    foreach (var script in LanguageTag.Scripts(tag).Where(media.Fonts.ContainsKey).Where(plannedFonts.Add))
                    {
                        AddCapability(features, $"Language.Fonts.{script}~~~und-{script.ToUpperInvariant()}~", $"{script} fonts for {tag}", state, media.FeaturesPath);
                    }
                }
            }
        }

        return new ServicingPlan([.. packs, .. features], warnings, errors);
    }

    /// <summary>Plans WinPE language resources for boot.wim or WinRE.</summary>
    /// <param name="installedPackages">Package names in the WinPE image, in install order, from DISM GetPackages.</param>
    public static ServicingPlan PlanWinPE(IEnumerable<string> languages, LanguageMedia media, IReadOnlyList<string> installedPackages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(installedPackages);

        var actions = new List<ServicingAction>();
        var warnings = new List<string>();

        // "WinPE-Setup-Client-Package~31bf3856ad364e35~amd64~~10.0.26100.1" -> "WinPE-Setup-Client"
        var components = installedPackages
            .Select(p => p.Split('~')[0])
            .Where(p => p.StartsWith("WinPE-", StringComparison.OrdinalIgnoreCase) && p.EndsWith("-Package", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[..^"-Package".Length])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var requested in languages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tag = LanguageTag.Normalize(requested);
            if (!media.WinPE.TryGetValue(tag, out var winPE))
            {
                // Expected for interface packs and input-only languages; Setup and WinRE keep their base language.
                warnings.Add($"{tag} has no WinPE language pack; Setup and recovery will not be shown in it.");
                continue;
            }

            if (WinPEFontLanguages.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                if (media.WinPEFontSupport.TryGetValue(tag, out var font))
                {
                    actions.Add(new AddPackageAction(font, $"WinPE font support {tag}"));
                }
                else
                {
                    warnings.Add($"WinPE font support for {tag} was not found; setup text may not render.");
                }
            }

            actions.Add(new AddPackageAction(winPE.LanguagePackPath, $"WinPE language pack {tag}"));

            foreach (var component in components)
            {
                if (winPE.Components.TryGetValue(component, out var path))
                {
                    actions.Add(new AddPackageAction(path, $"{component} {tag}"));
                }
            }
        }

        return new ServicingPlan(actions, warnings, []);
    }

    /// <summary>Full language packs for the same language (en-GB, en-US for en-CA) that are requested or already installed.</summary>
    private static List<string> FindParentLanguages(string tag, IEnumerable<string> requested, LanguageMedia media, ImageLanguageState? state)
    {
        var language = tag.Split('-')[0];
        bool SameLanguage(string other) =>
            !string.Equals(other, tag, StringComparison.OrdinalIgnoreCase)
            && string.Equals(other.Split('-')[0], language, StringComparison.OrdinalIgnoreCase);

        return requested.Select(LanguageTag.Normalize)
            .Where(t => SameLanguage(t) && media.LanguagePacks.TryGetValue(t, out var p) && !p.IsInterfacePack)
            .Concat(state?.InstalledLanguagePacks.Where(SameLanguage) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddCapability(
        List<ServicingAction> actions, string prefix, string description, ImageLanguageState? state, string source)
    {
        string name;
        if (state is null)
        {
            name = prefix + "0.0.1.0";
        }
        else
        {
            var match = state.Capabilities.FirstOrDefault(c => c.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                name = prefix + "0.0.1.0";
            }
            else if (match.Value)
            {
                return;
            }
            else
            {
                name = match.Key;
            }
        }

        actions.Add(new AddCapabilityAction(name, source, description));
    }
}
