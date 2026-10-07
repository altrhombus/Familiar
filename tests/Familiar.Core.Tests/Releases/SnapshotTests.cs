using Familiar.Core.Releases;

namespace Familiar.Core.Tests.Releases;

/// <summary>Checks the bundled DataForGeeks snapshot parses and produces the expected shape.</summary>
public class SnapshotTests
{
    private static readonly ReleaseCatalog Catalog = ReleaseDataSource.LoadSnapshot();

    [Theory]
    [InlineData("Windows 11 24H2", 26100)]
    [InlineData("Windows 11 25H2", 26100)]
    [InlineData("Windows 11 23H2", 22621)]
    [InlineData("Windows 10 22H2", 19041)]
    [InlineData("Windows 10 1909", 18362)]
    [InlineData("Windows Server 2025", 26100)]
    [InlineData("Windows Server 2022", 20348)]
    public void KnownBranches_HaveExpectedBaselines(string name, int baseline)
    {
        Assert.Equal(baseline, Catalog.Releases.Single(r => r.Name == name).ServicingBaselineBuild);
    }

    [Fact]
    public void EveryRelease_HasAtLeastOneUpdate_ExceptBrandNewOnes()
    {
        var withoutUpdates = Catalog.Releases.Where(r => Catalog.GetLatestUpdate(r, includePreview: true) is null).ToList();

        Assert.True(withoutUpdates.Count <= 2, $"Releases without updates: {string.Join(", ", withoutUpdates)}");
    }

    [Fact]
    public void Windows11_25H2_ReleaseMedia_ResolvesToItsCumulativeUpdate()
    {
        // The 25H2 ISO ships at 26200.6584, built from KB5065426 (published for 24H2 only).
        var release = Catalog.Resolve(26200, "Client")!;

        Assert.Equal("KB5065426", Catalog.FindInstalledUpdate(release, 6584)?.KbArticle);
    }

    [Fact]
    public void Snapshot_HasCollectionDate()
    {
        Assert.NotNull(Catalog.CollectedAt);
    }
}
