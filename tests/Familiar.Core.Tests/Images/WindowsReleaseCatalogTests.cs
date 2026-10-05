using Familiar.Core.Images;

namespace Familiar.Core.Tests.Images;

public class WindowsReleaseCatalogTests
{
    [Theory]
    [InlineData(26100, "Client", "Windows 11 24H2")]
    [InlineData(26100, "Server", "Windows Server 2025")]
    [InlineData(26100, "Server Core", "Windows Server 2025")]
    [InlineData(17763, "Client", "Windows 10 1809")]
    [InlineData(17763, "Server", "Windows Server 2019")]
    [InlineData(22631, "Client", "Windows 11 23H2")]
    [InlineData(19045, "Client", "Windows 10 22H2")]
    public void Resolve_ByBuild_DistinguishesClientAndServer(int build, string installationType, string expected)
    {
        var release = WindowsReleaseCatalog.Resolve(build, installationType);

        Assert.Equal(expected, release?.Name);
    }

    [Fact]
    public void Resolve_PrefersRegistryDisplayVersion_OverBaseBuild()
    {
        // 20H2 media can report the 19041 base build; the registry says otherwise.
        var release = WindowsReleaseCatalog.Resolve(19041, "Client", "20H2");

        Assert.Equal("Windows 10 20H2", release?.Name);
    }

    [Fact]
    public void Resolve_TreatsReleaseId2009As20H2()
    {
        var release = WindowsReleaseCatalog.Resolve(19041, "Client", "2009");

        Assert.Equal("Windows 10 20H2", release?.Name);
    }

    [Fact]
    public void Resolve_UnknownDisplayVersion_FallsBackToBuild()
    {
        var release = WindowsReleaseCatalog.Resolve(26100, "Client", "99H9");

        Assert.Equal("Windows 11 24H2", release?.Name);
    }

    [Fact]
    public void Resolve_UnknownBuild_ReturnsNull()
    {
        Assert.Null(WindowsReleaseCatalog.Resolve(12345, "Client"));
    }

    [Theory]
    [InlineData("Windows 10 22H2", "2004")]
    [InlineData("Windows 10 1909", "1903")]
    [InlineData("Windows 11 23H2", "22H2")]
    [InlineData("Windows 11 25H2", "24H2")]
    public void LanguageMedia_IsSharedAcrossEnablementReleases(string name, string expectedMedia)
    {
        var release = WindowsReleaseCatalog.All.Single(r => r.Name == name);

        Assert.Equal(expectedMedia, release.LanguageMediaVersion);
    }

    [Fact]
    public void Catalog_HasNoDuplicateFamilyAndBuild()
    {
        var duplicates = WindowsReleaseCatalog.All
            .GroupBy(r => (r.Family, r.Build))
            .Where(g => g.Count() > 1);

        Assert.Empty(duplicates);
    }
}
