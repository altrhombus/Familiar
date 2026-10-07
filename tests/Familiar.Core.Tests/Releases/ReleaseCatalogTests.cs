using Familiar.Core.Releases;

namespace Familiar.Core.Tests.Releases;

/// <summary>Behavior against small, hand-built data so the rules are explicit.</summary>
public class ReleaseCatalogTests
{
    private static ReleaseCatalog Catalog() => TestData.Catalog(
        releases: """
            { "os_type": "client", "major_version": 10, "windows_version": "1507 (RTM)", "os_build": "10240" },
            { "os_type": "client", "major_version": 10, "windows_version": "2004", "os_build": "19041" },
            { "os_type": "client", "major_version": 10, "windows_version": "20H2", "os_build": "19042" },
            { "os_type": "client", "major_version": 11, "windows_version": "21H2", "os_build": "22000" },
            { "os_type": "client", "major_version": 11, "windows_version": "24H2", "os_build": "26100" },
            { "os_type": "client", "major_version": 11, "windows_version": "25H2", "os_build": "26200" },
            { "os_type": "client", "major_version": 11, "windows_version": "27H2", "os_build": "26400" }
            """,
        builds: """
            { "build": "19041.1", "os_type": "client", "major_version": 10, "windows_version": "2004", "release_date": "2020-05-27", "kb_article": "KB1", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "19042.1", "os_type": "client", "major_version": 10, "windows_version": "20H2", "release_date": "2020-05-27", "kb_article": "KB1", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26100.5000", "os_type": "client", "major_version": 11, "windows_version": "24H2", "release_date": "2026-08-11", "kb_article": "KB50", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26200.5000", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2026-08-11", "kb_article": "KB50", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26200.5100", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2026-08-25", "kb_article": "KB51", "release_type": "Preview", "is_expired": false, "article_url": null },
            { "build": "26200.5150", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2026-08-30", "kb_article": "KB52", "release_type": "Hotpatch", "is_expired": false, "article_url": null },
            { "build": "26100.9000", "os_type": "server", "major_version": 2025, "windows_version": "2025", "release_date": "2026-08-11", "kb_article": "KB50", "release_type": "Standard", "is_expired": false, "article_url": "https://support.microsoft.com/help/50" },
            { "build": "25398.100", "os_type": "server", "major_version": 2022, "windows_version": "23H2", "release_date": "2026-08-11", "kb_article": "KB60", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "22000.300", "os_type": "client", "major_version": 11, "windows_version": "21H2", "release_date": "2026-08-11", "kb_article": "KB70", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26200.301", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2026-08-11", "kb_article": "KB70", "release_type": "Standard", "is_expired": false, "article_url": null }
            """,
        clientLifecycle: """
            { "version": "25H2", "sku": "Windows 11 Enterprise and Education", "start_date": "9/30/2025 ", "end_date": "10/10/2028" },
            { "version": "25H2", "sku": "Windows 11 Home and Pro", "start_date": "9/30/2025", "end_date": "10/12/2027 " }
            """,
        ltscLifecycle: """
            { "version": "24H2", "servicing_option": "Long-Term Servicing Channel (LTSC)", "build": "26100", "start_date": "2024-10-01", "mainstream_end_date": "2029-10-09", "extended_end_date": "2034-10-10" }
            """,
        serverLifecycle: """
            { "version": "Windows Server 2025", "tier": "Windows Server 2025", "start_date": "11/1/2024 ", "mainstream_end_date": "11/14/2029", "extended_end_date": "11/15/2034 " },
            { "version": "Windows Server 2025", "tier": "Extended Security Update Year 1", "start_date": "11/15/2034", "mainstream_end_date": "11/14/2035", "extended_end_date": "11/14/2035" }
            """);

    [Fact]
    public void EnablementReleases_ShareTheEarliestBuildAsBaseline()
    {
        var catalog = Catalog();

        Assert.Equal(26100, Find(catalog, "Windows 11 25H2").ServicingBaselineBuild);
        Assert.Equal(19041, Find(catalog, "Windows 10 20H2").ServicingBaselineBuild);
        Assert.True(Find(catalog, "Windows 11 25H2").SharesBaseline);
        Assert.False(Find(catalog, "Windows 11 24H2").SharesBaseline);
    }

    [Fact]
    public void ClientAndServer_SharingAKb_StayOnSeparateBranches()
    {
        var server = Find(Catalog(), "Windows Server 2025");

        Assert.Equal(26100, server.ServicingBaselineBuild);
        Assert.Equal(WindowsOsType.Server, server.OsType);
    }

    [Fact]
    public void SharedKb_WithDifferentRevisions_DoesNotMergeBranches()
    {
        // KB70 is mislabeled on two unrelated releases at different revisions.
        Assert.Equal(22000, Find(Catalog(), "Windows 11 21H2").ServicingBaselineBuild);
    }

    [Fact]
    public void ReleaseWithoutUpdateHistory_IsItsOwnBaseline()
    {
        Assert.Equal(26400, Find(Catalog(), "Windows 11 27H2").ServicingBaselineBuild);
    }

    [Fact]
    public void ExplicitBaseBuild_OverridesInference()
    {
        var catalog = TestData.Catalog(releases: """
            { "os_type": "client", "major_version": 11, "windows_version": "24H2", "os_build": "26100" },
            { "os_type": "client", "major_version": 11, "windows_version": "27H2", "os_build": "26400", "base_build": "26100" }
            """);

        Assert.Equal(26100, Find(catalog, "Windows 11 27H2").ServicingBaselineBuild);
    }

    [Fact]
    public void ServerReleases_AreDerivedFromUpdateHistory()
    {
        var names = Catalog().Releases.Where(r => r.OsType == WindowsOsType.Server).Select(r => r.Name);

        Assert.Equal(["Windows Server, version 23H2", "Windows Server 2025"], names);
    }

    [Fact]
    public void QualifiedVersions_AreNormalized()
    {
        Assert.Equal("1507", Find(Catalog(), "Windows 10 1507").Version);
    }

    [Theory]
    [InlineData(26100, "Client", null, "Windows 11 24H2")]
    [InlineData(26100, "Server Core", null, "Windows Server 2025")]
    [InlineData(19041, "Client", "20H2", "Windows 10 20H2")]
    [InlineData(19041, "Client", "2009", "Windows 10 20H2")]
    [InlineData(26100, "Client", "99H9", "Windows 11 24H2")]
    public void Resolve_UsesDisplayVersionThenBuild(int build, string installationType, string? displayVersion, string expected)
    {
        Assert.Equal(expected, Catalog().Resolve(build, installationType, displayVersion)?.Name);
    }

    [Fact]
    public void Resolve_DisplayVersion_DoesNotCrossWindowsGenerations()
    {
        // "21H2" exists for both Windows 10 and 11; a 19041-based image is Windows 10.
        Assert.Null(Catalog().Resolve(19041, "Client", "21H2")?.MajorVersion is 11 ? "wrong" : null);
    }

    [Fact]
    public void Resolve_UnknownBuild_ReturnsNull()
    {
        Assert.Null(Catalog().Resolve(12345, "Client"));
    }

    [Fact]
    public void LatestUpdate_SkipsPreviewAndHotpatch_ByDefault()
    {
        var catalog = Catalog();
        var release = Find(catalog, "Windows 11 25H2");

        Assert.Equal("KB50", catalog.GetLatestUpdate(release)?.KbArticle);
        Assert.Equal("KB51", catalog.GetLatestUpdate(release, includePreview: true)?.KbArticle);
    }

    [Fact]
    public void FindInstalledUpdate_MatchesRevision()
    {
        var catalog = Catalog();

        Assert.Equal("KB50", catalog.FindInstalledUpdate(Find(catalog, "Windows 11 25H2"), 5000)?.KbArticle);
        Assert.Null(catalog.FindInstalledUpdate(Find(catalog, "Windows 11 25H2"), 1));
    }

    [Fact]
    public void FindInstalledUpdate_FallsBackToSameRevisionOnTheBranch()
    {
        // 25H2 media built from a CU that was only published for 24H2.
        var catalog = TestData.Catalog(
            releases: """
                { "os_type": "client", "major_version": 11, "windows_version": "24H2", "os_build": "26100" },
                { "os_type": "client", "major_version": 11, "windows_version": "25H2", "os_build": "26200" }
                """,
            builds: """
                { "build": "26100.6584", "os_type": "client", "major_version": 11, "windows_version": "24H2", "release_date": "2025-09-09", "kb_article": "KB5065426", "release_type": "Standard", "is_expired": false, "article_url": null },
                { "build": "26100.6899", "os_type": "client", "major_version": 11, "windows_version": "24H2", "release_date": "2025-10-14", "kb_article": "KB5066835", "release_type": "Standard", "is_expired": false, "article_url": null },
                { "build": "26200.6899", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2025-10-14", "kb_article": "KB5066835", "release_type": "Standard", "is_expired": false, "article_url": null },
                { "build": "26100.6584", "os_type": "server", "major_version": 2025, "windows_version": "2025", "release_date": "2025-09-09", "kb_article": "KB-SERVER", "release_type": "Standard", "is_expired": false, "article_url": null }
                """);

        Assert.Equal("KB5065426", catalog.FindInstalledUpdate(Find(catalog, "Windows 11 25H2"), 6584)?.KbArticle);
    }

    [Theory]
    [InlineData("Enterprise", "2028-10-10")]
    [InlineData("Education", "2028-10-10")]
    [InlineData("Professional", "2027-10-12")]
    [InlineData(null, "2027-10-12")]
    public void ClientLifecycle_DependsOnEdition(string? editionId, string expectedEnd)
    {
        var catalog = Catalog();

        var lifecycle = catalog.GetLifecycle(Find(catalog, "Windows 11 25H2"), editionId);

        Assert.Equal(DateOnly.Parse(expectedEnd, System.Globalization.CultureInfo.InvariantCulture), lifecycle?.EndDate);
    }

    [Fact]
    public void LtscLifecycle_MatchesByBuild()
    {
        var catalog = Catalog();

        var lifecycle = catalog.GetLifecycle(Find(catalog, "Windows 11 24H2"), "EnterpriseS");

        Assert.Equal(new DateOnly(2034, 10, 10), lifecycle?.EndDate);
    }

    [Fact]
    public void ServerLifecycle_IgnoresEsuTiers()
    {
        var catalog = Catalog();

        var lifecycle = catalog.GetLifecycle(Find(catalog, "Windows Server 2025"), "ServerDatacenter");

        Assert.Equal(new DateOnly(2034, 11, 15), lifecycle?.EndDate);
    }

    [Theory]
    [InlineData("2024-10-01", 2024, 10, 1)]
    [InlineData("11/1/2024 ", 2024, 11, 1)]
    [InlineData("8/2/2016 8", 2016, 8, 2)]
    public void TryParseDate_ToleratesUpstreamFormats(string value, int year, int month, int day)
    {
        Assert.True(ReleaseCatalog.TryParseDate(value, out var date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData("End of updates")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseDate_RejectsNonDates(string? value)
    {
        Assert.False(ReleaseCatalog.TryParseDate(value, out _));
    }

    private static WindowsRelease Find(ReleaseCatalog catalog, string name) =>
        catalog.Releases.Single(r => r.Name == name);
}
