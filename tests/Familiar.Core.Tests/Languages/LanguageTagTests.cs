using Familiar.Core.Languages;

namespace Familiar.Core.Tests.Languages;

public class LanguageTagTests
{
    [Theory]
    [InlineData("de-de", "de-DE")]
    [InlineData("sr-latn-rs", "sr-Latn-RS")]
    [InlineData("ZH-tw", "zh-TW")]
    [InlineData("kok-deva-in", "kok-Deva-IN")]
    public void Normalize_UsesCanonicalCasing(string input, string expected)
    {
        Assert.Equal(expected, LanguageTag.Normalize(input));
    }

    [Theory]
    [InlineData("ja-JP", "Jpan")]
    [InlineData("zh-CN", "Hans")]
    [InlineData("zh-TW", "Hant")]
    [InlineData("ko-KR", "Kore")]
    [InlineData("hi-IN", "Deva")]
    [InlineData("de-DE", "Latn")]
    public void Scripts_ComeFromWindowsLocaleData(string tag, string script)
    {
        Assert.Contains(script, LanguageTag.Scripts(tag));
    }

    [Fact]
    public void Scripts_UnknownLocale_DoesNotPickAFontScript()
    {
        // Windows synthesizes unknown locales as Latin, which has no font Feature on Demand.
        Assert.DoesNotContain(LanguageTag.Scripts("qq-QQ"), s => s is "Jpan" or "Hans" or "Hant" or "Kore" or "Arab");
    }
}
