using Familiar.Core.ConfigMgr;

namespace Familiar.Core.Tests.ConfigMgr;

public class PackageTemplateTests
{
    private static readonly ImageFacts Facts = new("25H2", "AMD64", "Enterprise", "26200.9457", "KB5129195", new DateOnly(2026, 9, 14));
    private static readonly DateTime Now = new(2026, 10, 6, 9, 30, 0);

    [Theory]
    [InlineData("Windows 11 {arch}", "Windows 11 AMD64")]
    [InlineData("{release}", "25H2")]
    [InlineData("{cu:yyyy-MM}", "2026-09")]
    [InlineData("{cu}", "2026-09")]
    [InlineData("Windows 11 {release} {edition} {arch}", "Windows 11 25H2 Enterprise AMD64")]
    [InlineData("{build} ({kb})", "26200.9457 (KB5129195)")]
    [InlineData("Built {date:yyyy-MM-dd}", "Built 2026-10-06")]
    [InlineData("{date}", "2026.10.06")]
    [InlineData("no tokens", "no tokens")]
    public void Expand_FillsTokensFromTheImage(string template, string expected)
    {
        Assert.Equal(expected, PackageTemplate.Expand(template, Facts, Now));
    }

    [Fact]
    public void Expand_UnknownUpdate_ExplainsWhy()
    {
        var ex = Assert.Throws<ConfigMgrException>(() => PackageTemplate.Expand("{cu:yyyy-MM}", Facts with { UpdateDate = null }, Now));

        Assert.Contains("could not be determined", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{release}", true)]
    [InlineData("{cu:yyyy-MM}", true)]
    [InlineData("{kb}", true)]
    [InlineData("{date:yyMM}", false)]
    [InlineData("Windows 11", false)]
    [InlineData(null, false)]
    public void NeedsImageFacts(string? template, bool expected)
    {
        Assert.Equal(expected, PackageTemplate.NeedsImageFacts(template));
    }

    [Theory]
    [InlineData("{release}", false, 0)]
    [InlineData("{n}", false, 1)]
    [InlineData("{n}", true, 0)]
    [InlineData("{bogus}", false, 1)]
    [InlineData("{release:yy}", false, 1)]
    public void Validate(string template, bool allowCounter, int problems)
    {
        Assert.Equal(problems, PackageTemplate.Validate(template, allowCounter).Count);
    }

    [Theory]
    [InlineData("26200.6584", "10.0.26200.7628", true)]
    [InlineData("26200.9457", "10.0.26200.7628", false)]
    [InlineData("26200.7628", "10.0.26200.7628", false)]
    [InlineData("26100.9999", "10.0.26200.1", true)]
    [InlineData("26200.6584", null, false)]
    [InlineData(null, "10.0.26200.7628", false)]
    public void IsOlder_ComparesBuildThenRevision(string? newBuild, string? current, bool older)
    {
        Assert.Equal(older, ConfigMgrPublisher.IsOlder(newBuild, current) is not null);
    }
}
