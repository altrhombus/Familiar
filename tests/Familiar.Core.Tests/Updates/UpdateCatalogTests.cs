using Familiar.Core.Releases;
using Familiar.Core.Updates;

namespace Familiar.Core.Tests.Updates;

/// <summary>Parsing against excerpts saved from the live Microsoft Update Catalog (October 2026).</summary>
public class UpdateCatalogTests
{
    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Updates", "Fixtures");

    private static readonly WindowsRelease Windows11_25H2 = new(WindowsOsType.Client, 11, "25H2", 26200, 26100);
    private static readonly WindowsRelease Windows10_22H2 = new(WindowsOsType.Client, 10, "22H2", 19045, 19041);

    [Fact]
    public void ParseSearch_ReadsEveryRow()
    {
        var entries = UpdateCatalogClient.ParseSearch(Fixture("search-kb5129195.html"));

        Assert.Equal(6, entries.Count);
        var x64 = entries.Single(e => e.UpdateId == "5cc91450-4f0f-40a8-a67a-63dc58d9dac9");
        Assert.Equal("2026-09 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5129195) (26200.9457)", x64.Title);
        Assert.Equal("Windows 11", x64.Products);
        Assert.Equal("Security Updates", x64.Classification);
        Assert.Equal(new DateOnly(2026, 9, 14), x64.LastUpdated);
        Assert.Equal(5173184334, x64.SizeBytes);
    }

    [Fact]
    public void ParseDownloadDialog_ReturnsTargetAndCheckpoint()
    {
        var files = UpdateCatalogClient.ParseDownloadDialog(Fixture("download-dialog-kb5129195.html"));

        Assert.Equal(
            ["windows11.0-kb5129195-x64_ed361878ec2b56a7dfdb8f256565263a7fdc8eaa.msu", "windows11.0-kb5043080-x64_953449672073f8fb99badb4cc6d5d7849b9c83e8.msu"],
            files.Select(f => f.FileName));
        Assert.All(files, f => Assert.Equal("catalog.sf.dl.delivery.mp.microsoft.com", f.Url.Host));
        Assert.Equal("7TYYeOwrVqff248lZWUmOn/cjqo=", files[0].Sha1);
    }

    [Fact]
    public void CatalogDigest_IsTheShaInTheFileName()
    {
        // Update Catalog file names end in the SHA-1 the dialog publishes as base64.
        var file = UpdateCatalogClient.ParseDownloadDialog(Fixture("download-dialog-kb5129195.html"))[0];

        Assert.Equal("ed361878ec2b56a7dfdb8f256565263a7fdc8eaa", Convert.ToHexStringLower(Convert.FromBase64String(file.Sha1!)));
    }

    [Theory]
    [InlineData("2026-09 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5129195) (26200.9457)", "x64", true)]
    [InlineData("2026-09 Cumulative Update for Windows 11, version 25H2 for arm64-based Systems (KB5129195) (26200.9457)", "x64", false)]
    [InlineData("2026-09 Cumulative Update for Windows 11, version 26H2 for x64-based Systems (KB5129195) (26300.9457)", "x64", false)]
    [InlineData("2026-09 Dynamic Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5129195)", "x64", false)]
    [InlineData("2026-08 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5120000) (26200.9000)", "x64", false)]
    public void IsCumulativeUpdateFor_MatchesReleaseArchitectureAndKb(string title, string architecture, bool expected)
    {
        Assert.Equal(expected, UpdateDownloader.IsCumulativeUpdateFor(title, Windows11_25H2, architecture, "KB5129195"));
    }

    [Fact]
    public void IsCumulativeUpdateFor_AcceptsWindows10TitleStyle()
    {
        Assert.True(UpdateDownloader.IsCumulativeUpdateFor(
            "2026-09 Cumulative Update for Windows 10 Version 22H2 for x64-based Systems (KB5129236)", Windows10_22H2, "x64", "KB5129236"));
    }

    [Theory]
    [InlineData("2026-09 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 25H2 for x64 (KB5126052)", true)]
    [InlineData("2026-08 Cumulative Update Preview for .NET Framework 3.5 and 4.8.1 for Windows 11, version 25H2 for x64 (KB5122385)", true)]
    [InlineData("2026-09 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 24H2 for x64 (KB5126050)", false)]
    [InlineData("2026-09 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 25H2 for ARM64 (KB5126053)", false)]
    public void IsDotNetUpdateFor_MatchesReleaseAndArchitecture(string title, bool expected)
    {
        Assert.Equal(expected, UpdateDownloader.IsDotNetUpdateFor(title, Windows11_25H2, "x64"));
    }

    [Theory]
    [InlineData("2026-09 Safe OS Dynamic Update for Windows 11, version 25H2 for x64-based Systems (KB5125758)", true)]
    [InlineData("2026-04 Safe OS Dynamic Update for Windows 11 Version 25H2 for x64-based Systems (KB5084812)", true)]
    [InlineData("2026-09 Safe OS Dynamic Update for Windows 11, version 25H2 for arm64-based Systems (KB5125758)", false)]
    [InlineData("2026-09 Safe OS Dynamic Update for Windows 11, version 24H2 for x64-based Systems (KB5125758)", false)]
    [InlineData("2026-09 Setup Dynamic Update for Windows 11, version 25H2 for x64-based Systems (KB5125759)", false)]
    public void IsSafeOSUpdateFor_MatchesReleaseAndArchitecture(string title, bool expected)
    {
        Assert.Equal(expected, UpdateDownloader.IsSafeOSUpdateFor(title, Windows11_25H2, "x64"));
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureFolder, name));
}
