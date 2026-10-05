using Familiar.Core.Releases;
using Familiar.Core.Servicing;
using Familiar.Core.Tests.Releases;
using Familiar.Core.Updates;

namespace Familiar.Core.Tests.Updates;

public sealed class UpdatePlannerTests : IDisposable
{
    private const string Checkpoint = @"C:\u\windows11.0-kb5043080-x64_953449672073f8fb99badb4cc6d5d7849b9c83e8.msu";
    private const string Target = @"C:\u\windows11.0-kb5129195-x64_0123456789abcdef0123456789abcdef01234567.msu";
    private const string DotNet = @"C:\u\windows11.0-kb5129999-x64-ndp481_aaaaaaaa.msu";
    private const string Arm = @"C:\u\windows11.0-kb5129195-arm64_bbbbbbbb.msu";
    private const string Hotpatch = @"C:\u\windows11.0-kb5129300-x64_cccccccc.msu";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));

    private static readonly ReleaseCatalog Catalog = TestData.Catalog(
        releases: """
            { "os_type": "client", "major_version": 11, "windows_version": "24H2", "os_build": "26100" },
            { "os_type": "client", "major_version": 11, "windows_version": "25H2", "os_build": "26200" }
            """,
        builds: """
            { "build": "26100.1742", "os_type": "client", "major_version": 11, "windows_version": "24H2", "release_date": "2024-09-10", "kb_article": "KB5043080", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26100.6584", "os_type": "client", "major_version": 11, "windows_version": "24H2", "release_date": "2025-09-09", "kb_article": "KB5065426", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26100.9457", "os_type": "client", "major_version": 11, "windows_version": "24H2", "release_date": "2026-09-14", "kb_article": "KB5129195", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26200.9457", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2026-09-14", "kb_article": "KB5129195", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26200.9500", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2026-09-20", "kb_article": "KB5129300", "release_type": "Hotpatch", "is_expired": false, "article_url": null }
            """);

    private static readonly WindowsRelease Release25H2 = Catalog.Releases.Single(r => r.Version == "25H2");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Plan_PutsCheckpointBeforeTarget_AndUnknownLast()
    {
        var plan = UpdatePlanner.Plan(Files(DotNet, Target, Checkpoint), new UpdateTarget(Release25H2, "x64", InstalledRevision: null), Catalog);

        Assert.Equal([Checkpoint, Target, DotNet], Paths(plan));
    }

    [Fact]
    public void Plan_CheckpointFromTheBaselineRelease_IsRecognizedForEnablementRelease()
    {
        // KB5043080 only lists 24H2, but 25H2 is on the same branch.
        var plan = UpdatePlanner.Plan(Files(Checkpoint), new UpdateTarget(Release25H2, "x64", null), Catalog);

        Assert.Contains("KB5043080 cumulative update", plan.Actions.Single().Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_SkipsOtherArchitectures()
    {
        var plan = UpdatePlanner.Plan(Files(Arm, Target), new UpdateTarget(Release25H2, "x64", 6584), Catalog);

        Assert.Equal([Target], Paths(plan));
        Assert.Contains(plan.Warnings, w => w.Contains("arm64", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_SkipsHotpatches()
    {
        var plan = UpdatePlanner.Plan(Files(Hotpatch, Target), new UpdateTarget(Release25H2, "x64", 6584), Catalog);

        Assert.Equal([Target], Paths(plan));
        Assert.Contains(plan.Warnings, w => w.Contains("Hotpatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_SkipsUpdatesTheImageAlreadyHas()
    {
        var plan = UpdatePlanner.Plan(Files(Checkpoint, Target), new UpdateTarget(Release25H2, "x64", 6584), Catalog);

        Assert.Equal([Target], Paths(plan));
        Assert.Contains(plan.Warnings, w => w.Contains("already at revision 6584", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_WarnsWhenLatestUpdateIsMissing()
    {
        var plan = UpdatePlanner.Plan(Files(Checkpoint), new UpdateTarget(Release25H2, "x64", null), Catalog);

        Assert.Contains(plan.Warnings, w => w.Contains("KB5129195", StringComparison.Ordinal) && w.Contains("not in the updates folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_NoLatestWarning_WhenTargetPresent()
    {
        var plan = UpdatePlanner.Plan(Files(Target), new UpdateTarget(Release25H2, "x64", 6584), Catalog);

        Assert.DoesNotContain(plan.Warnings, w => w.Contains("not in the updates folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_WithoutReleaseData_AppliesEverythingInKbOrder()
    {
        var plan = UpdatePlanner.Plan(Files(Target, Checkpoint), new UpdateTarget(null, "x64", null), catalog: null);

        Assert.Equal([Checkpoint, Target], Paths(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Scan_FindsMsuAndCab_WithKbNumbers()
    {
        Directory.CreateDirectory(_folder);
        foreach (var name in new[] { Path.GetFileName(Checkpoint), "windows11.0-kb5129195-x64.cab", "readme.txt", "notes.msu" })
        {
            File.WriteAllBytes(Path.Combine(_folder, name), []);
        }

        var files = UpdatePlanner.Scan(_folder);

        Assert.Equal(["KB5043080", "KB5129195"], files.Select(f => f.Kb).Order());
        Assert.All(files, f => Assert.Equal("x64", f.Architecture));
    }

    [Theory]
    [InlineData("windows11.0-kb5043080-x64_953449672073f8fb99badb4cc6d5d7849b9c83e8.msu", "KB5043080", "x64")]
    [InlineData("windows11.0-kb5129195-arm64_abc.msu", "KB5129195", "arm64")]
    [InlineData("Windows10.0-KB5011048-x64-NDP48.msu", "KB5011048", "x64")]
    [InlineData("ssu-26100.1-kb5100000.cab", "KB5100000", null)]
    public void Parse_ReadsKbAndArchitecture(string name, string kb, string? architecture)
    {
        var file = UpdatePlanner.Parse(Path.Combine(@"C:\u", name));

        Assert.Equal(kb, file?.Kb);
        Assert.Equal(architecture, file?.Architecture);
    }

    private static UpdateFile[] Files(params string[] paths) => paths.Select(p => UpdatePlanner.Parse(p)!).ToArray();

    private static List<string> Paths(ServicingPlan plan) =>
        plan.Actions.Cast<AddPackageAction>().Select(a => a.PackagePath).ToList();
}
