using Familiar.Core.ConfigMgr;

namespace Familiar.Core.Tests.ConfigMgr;

public class SourceFolderTemplateTests
{
    private const string Parent = @"\\cm01\Source$\OSImages\Win11";
    private static readonly DateTime October = new(2026, 10, 6);

    [Fact]
    public void Expand_MatchesTheReleaseArchDateCounterConvention()
    {
        var path = SourceFolderTemplate.Expand("{release}_{arch}_{date:yyMM}_{n}", Parent, "25H2", "AMD64", October, _ => false);

        Assert.Equal(@"\\cm01\Source$\OSImages\Win11\25H2_AMD64_2610_1", path);
    }

    [Fact]
    public void Expand_CountsPastExistingFolders()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Parent + @"\25H2_AMD64_2610_1", Parent + @"\25H2_AMD64_2610_2" };

        var path = SourceFolderTemplate.Expand("{release}_{arch}_{date:yyMM}_{n}", Parent, "25H2", "AMD64", October, taken.Contains);

        Assert.EndsWith(@"\25H2_AMD64_2610_3", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_WithoutCounter_RefusesToReuseAFolder()
    {
        Assert.Throws<ConfigMgrException>(() => SourceFolderTemplate.Expand("{date:yyyyMMdd}", Parent, null, null, October, _ => true));
    }

    [Fact]
    public void Expand_DefaultDateFormat()
    {
        Assert.EndsWith(@"\2026.10.06", SourceFolderTemplate.Expand("{date}", Parent, null, null, October, _ => false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{release}.{n}")]
    [InlineData("{arch}.{n}")]
    public void Expand_MissingImageDetails_Explains(string template)
    {
        var ex = Assert.Throws<ConfigMgrException>(() => SourceFolderTemplate.Expand(template, Parent, null, null, October, _ => false));

        Assert.Contains("unknown", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{build}.{n}")]
    [InlineData("a/b{n}")]
    public void Expand_RejectsUnknownTokensAndInvalidNames(string template)
    {
        Assert.Throws<ConfigMgrException>(() => SourceFolderTemplate.Expand(template, Parent, "25H2", "AMD64", October, _ => false));
    }

    [Theory]
    [InlineData("{release}.{n}", true)]
    [InlineData("{ARCH}", true)]
    [InlineData("{date:yyMM}.{n}", false)]
    public void NeedsImageDetails(string template, bool expected)
    {
        Assert.Equal(expected, SourceFolderTemplate.NeedsImageDetails(template));
    }
}
