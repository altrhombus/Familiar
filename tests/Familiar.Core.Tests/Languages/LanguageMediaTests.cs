using Familiar.Core.Languages;

namespace Familiar.Core.Tests.Languages;

public sealed class LanguageMediaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Scan_RecognizesPacksFeaturesAndFonts_FromIsoLayout()
    {
        var media = LanguageMedia.Scan(LanguageFixtures.CreateIso(_root));

        Assert.EndsWith("LanguagesAndOptionalFeatures", media.FeaturesPath, StringComparison.Ordinal);
        Assert.False(media.LanguagePacks["de-DE"].IsInterfacePack);
        Assert.True(media.LanguagePacks["ca-ES"].IsInterfacePack);
        Assert.Equal([LanguageFeature.Basic, LanguageFeature.Handwriting, LanguageFeature.OCR, LanguageFeature.Speech, LanguageFeature.TextToSpeech],
            media.Features["de-DE"].Keys.Order());
        Assert.Contains("Jpan", media.Fonts.Keys);
        Assert.Contains("PanEuropeanSupplementalFonts", media.Fonts.Keys);
    }

    [Fact]
    public void Scan_NormalizesTagCasing()
    {
        var media = LanguageMedia.Scan(LanguageFixtures.CreateIso(_root));

        Assert.True(media.Features.ContainsKey("sr-Latn-RS"));
        Assert.Equal("sr-Latn-RS", media.Features.Keys.Single(k => k.StartsWith("sr", StringComparison.Ordinal)));
    }

    [Fact]
    public void Scan_IgnoresSatellitesAndOtherArchitectures()
    {
        var media = LanguageMedia.Scan(LanguageFixtures.CreateIso(_root));

        Assert.DoesNotContain(media.Languages, l => l.Equals("arm64", StringComparison.OrdinalIgnoreCase));
        Assert.False(media.LanguagePacks.ContainsKey("fr-FR"), "fr-FR only has an arm64 pack in the fixture");
    }

    [Fact]
    public void Scan_FindsWinPEComponentsBesideFeatures()
    {
        var media = LanguageMedia.Scan(LanguageFixtures.CreateIso(_root));

        var german = media.WinPE["de-DE"];
        Assert.EndsWith("lp.cab", german.LanguagePackPath, StringComparison.Ordinal);
        Assert.Contains("winpe-setup-client", german.Components.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ja-JP", media.WinPEFontSupport.Keys);
    }

    [Fact]
    public void Scan_AcceptsTheFeaturesFolderDirectly()
    {
        var root = LanguageFixtures.CreateIso(_root);

        var media = LanguageMedia.Scan(Path.Combine(root, "LanguagesAndOptionalFeatures"));

        Assert.Contains("de-DE", media.LanguagePacks.Keys);
        Assert.NotEmpty(media.WinPE);
    }

    [Fact]
    public void Scan_MissingFolder_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => LanguageMedia.Scan(Path.Combine(_root, "nope")));
    }
}
