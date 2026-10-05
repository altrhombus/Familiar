using Familiar.Core.Languages;
using Familiar.Core.Servicing;

namespace Familiar.Core.Tests.Languages;

public sealed class LanguagePlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));
    private readonly LanguageMedia _media;

    public LanguagePlannerTests()
    {
        _media = LanguageMedia.Scan(LanguageFixtures.CreateIso(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void PlanImage_AddsAllLanguagePacksBeforeAnyFeature()
    {
        var plan = LanguagePlanner.PlanImage(Request("de-DE", "ja-JP"), _media);

        var lastPack = plan.Actions.ToList().FindLastIndex(a => a is AddPackageAction);
        var firstFeature = plan.Actions.ToList().FindIndex(a => a is AddCapabilityAction);
        Assert.Equal(1, lastPack);
        Assert.Equal(2, firstFeature);
    }

    [Fact]
    public void PlanImage_OrdersFeaturesAsDocumented()
    {
        var plan = LanguagePlanner.PlanImage(Request("de-DE"), _media);

        Assert.Equal(
            [
                "Language.Basic~~~de-DE~0.0.1.0",
                "Language.OCR~~~de-DE~0.0.1.0",
                "Language.Handwriting~~~de-DE~0.0.1.0",
                "Language.TextToSpeech~~~de-DE~0.0.1.0",
                "Language.Speech~~~de-DE~0.0.1.0",
            ],
            Capabilities(plan));
        Assert.All(plan.Actions.OfType<AddCapabilityAction>(), a => Assert.Equal(_media.FeaturesPath, a.SourcePath));
    }

    [Fact]
    public void PlanImage_AddsScriptFontsAfterBasic_OncePerScript()
    {
        var plan = LanguagePlanner.PlanImage(Request("ja-JP", "zh-CN", "zh-TW"), _media);

        var capabilities = Capabilities(plan);
        Assert.Equal(capabilities.IndexOf("Language.Basic~~~ja-JP~0.0.1.0") + 1, capabilities.IndexOf("Language.Fonts.Jpan~~~und-JPAN~0.0.1.0"));
        Assert.Contains("Language.Fonts.Hans~~~und-HANS~0.0.1.0", capabilities);
        Assert.Contains("Language.Fonts.Hant~~~und-HANT~0.0.1.0", capabilities);
        Assert.Equal(capabilities.Count, capabilities.Distinct().Count());
    }

    [Fact]
    public void PlanImage_LatinLanguages_DoNotGetSupplementalFonts()
    {
        var plan = LanguagePlanner.PlanImage(Request("de-DE"), _media);

        Assert.DoesNotContain(Capabilities(plan), c => c.Contains("Fonts", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanImage_OnlyRequestedFeatures_PlusBasic()
    {
        var request = Request("de-DE") with { Features = new HashSet<LanguageFeature> { LanguageFeature.OCR } };

        var plan = LanguagePlanner.PlanImage(request, _media);

        Assert.Equal(["Language.Basic~~~de-DE~0.0.1.0", "Language.OCR~~~de-DE~0.0.1.0"], Capabilities(plan));
    }

    [Fact]
    public void PlanImage_SkipsFeaturesTheMediaDoesNotHave()
    {
        var plan = LanguagePlanner.PlanImage(Request("ja-JP"), _media);

        Assert.DoesNotContain(Capabilities(plan), c => c.StartsWith("Language.Speech", StringComparison.Ordinal));
        Assert.True(plan.IsValid);
    }

    [Fact]
    public void PlanImage_InterfacePack_Warns()
    {
        var plan = LanguagePlanner.PlanImage(Request("ca-ES"), _media);

        Assert.True(plan.IsValid);
        Assert.Contains(plan.Warnings, w => w.Contains("Language Interface Pack", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanImage_InputOnlyLanguage_WarnsAndAddsFeatures()
    {
        var plan = LanguagePlanner.PlanImage(Request("af-ZA"), _media);

        Assert.DoesNotContain(plan.Actions, a => a is AddPackageAction);
        Assert.Equal(["Language.Basic~~~af-ZA~0.0.1.0"], Capabilities(plan));
        Assert.Contains(plan.Warnings, w => w.Contains("no language pack", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanImage_UnknownLanguage_IsAnError()
    {
        var plan = LanguagePlanner.PlanImage(Request("de-DE", "xx-XX"), _media);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("xx-XX", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanImage_AcceptsAnyTagCasing_AndDeduplicates()
    {
        var plan = LanguagePlanner.PlanImage(Request("DE-de", "de-DE"), _media);

        Assert.Single(plan.Actions.OfType<AddPackageAction>());
    }

    [Fact]
    public void PlanImage_WithImageState_UsesExactNamesAndSkipsInstalled()
    {
        var state = new ImageLanguageState(
            new Dictionary<string, bool>
            {
                ["Language.Basic~~~de-DE~0.0.1.0"] = true,
                ["Language.OCR~~~de-DE~0.0.2.0"] = false,
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "de-DE" });
        var request = Request("de-DE") with { Features = new HashSet<LanguageFeature> { LanguageFeature.OCR } };

        var plan = LanguagePlanner.PlanImage(request, _media, state);

        Assert.Equal(["Language.OCR~~~de-DE~0.0.2.0"], Capabilities(plan));
    }

    [Fact]
    public void PlanWinPE_AddsLanguagePackThenInstalledComponentsOnly()
    {
        string[] installed =
        [
            "Microsoft-Windows-WinPE-Package~31bf3856ad364e35~amd64~~10.0.26100.1",
            "WinPE-Setup-Package~31bf3856ad364e35~amd64~~10.0.26100.1",
            "WinPE-Setup-Client-Package~31bf3856ad364e35~amd64~~10.0.26100.1",
        ];

        var plan = LanguagePlanner.PlanWinPE(["de-DE"], _media, installed);

        Assert.Equal(["lp.cab", "winpe-setup_de-de.cab", "winpe-setup-client_de-de.cab"], plan.Actions.Cast<AddPackageAction>().Select(a => Path.GetFileName(a.PackagePath)));
    }

    [Fact]
    public void PlanWinPE_EastAsian_AddsFontSupport()
    {
        var plan = LanguagePlanner.PlanWinPE(["ja-JP"], _media, ["WinPE-Setup-Package~31bf3856ad364e35~amd64~~10.0.26100.1"]);

        Assert.Equal("WinPE-FontSupport-JA-JP.cab", Path.GetFileName(((AddPackageAction)plan.Actions[0]).PackagePath));
    }

    [Fact]
    public void PlanWinPE_LanguageWithoutWinPEPack_Warns()
    {
        var plan = LanguagePlanner.PlanWinPE(["ca-ES"], _media, []);

        Assert.True(plan.IsValid);
        Assert.Empty(plan.Actions);
        Assert.Single(plan.Warnings);
    }

    private static LanguageRequest Request(params string[] languages) => new() { Languages = languages };

    private static List<string> Capabilities(ServicingPlan plan) =>
        plan.Actions.OfType<AddCapabilityAction>().Select(a => a.CapabilityName).ToList();
}
