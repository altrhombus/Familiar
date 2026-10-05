using Familiar.Core.ConfigMgr;

namespace Familiar.Core.Tests.ConfigMgr;

public sealed class ConfigMgrPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));
    private readonly string _wim;

    public ConfigMgrPublisherTests()
    {
        Directory.CreateDirectory(_root);
        _wim = Path.Combine(_root, "install-25h2.wim");
        File.WriteAllBytes(_wim, [1, 2, 3, 4]);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task NewPackage_CopiesCreatesReloadsAndDistributes()
    {
        var site = new FakeSite();
        var source = Path.Combine(_root, "share", "Win11");

        var result = await new ConfigMgrPublisher(site).PublishAsync(_wim, new ConfigMgrPublishRequest
        {
            Name = "Win11 25H2 de-ja",
            Version = "2026.10.05",
            PackageSourcePath = source,
            DistributionPointGroups = ["all dps"],
            DistributionPoints = ["dp01"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.Equal("PS100200", result.PackageId);
        Assert.Equal([4], [(int)new FileInfo(Path.Combine(source, "install-25h2.wim")).Length]);
        Assert.Equal(
            ["create Win11 25H2 de-ja 2026.10.05 " + Path.Combine(source, "install-25h2.wim"), "reload PS100200", "group PS100200 g1", "point PS100200 dp01.contoso.com"],
            site.Calls);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ExistingPackage_OverwritesSourceAndRefreshes_SkippingTargetedPoints()
    {
        var existingSource = Path.Combine(_root, "share", "existing", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(existingSource)!);
        File.WriteAllBytes(existingSource, [9]);
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, existingSource, "10.0.26200.6584") };
        site.Targets.Add(@"[""Display=\\dp01.contoso.com\""]MSWNET:[""SMS_SITE=PS1""]\\dp01.contoso.com\");

        var result = await new ConfigMgrPublisher(site).PublishAsync(_wim, new ConfigMgrPublishRequest
        {
            PackageId = "PS100123",
            Name = "ignored for updates",
            Version = "2026.10.05",
            DistributionPoints = ["dp01"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Created);
        Assert.Equal(4, new FileInfo(existingSource).Length);
        Assert.Equal(["update PS100123 2026.10.05", "reload PS100123", "refresh PS100123"], site.Calls);
        Assert.False(File.Exists(existingSource + ".familiar-partial"));
    }

    [Fact]
    public async Task ExistingPackage_KeepsReplacedImageForRollback()
    {
        var source = Path.Combine(_root, "share", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [9]);
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null) };

        var result = await new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { PackageId = "PS100123", Name = "x" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(source + ".previous", result.PreviousImagePath);
        Assert.Equal([9], File.ReadAllBytes(source + ".previous"));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(source));
    }

    [Fact]
    public async Task ExistingPackage_WithoutRefresh_LeavesDistributionPointsAlone()
    {
        var source = Path.Combine(_root, "install.wim");
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null) };

        var result = await new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { PackageId = "PS100123", Name = "x", RefreshDistributionPoints = false },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("refresh PS100123", site.Calls);
        Assert.Contains("reload PS100123", site.Calls);
        Assert.Contains(result.Warnings, w => w.Contains("still serve the previous content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rollback_RestoresPreviousImage_KeepsRejectedOne()
    {
        var source = Path.Combine(_root, "share", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [9]);
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null) };
        var publisher = new ConfigMgrPublisher(site);
        await publisher.PublishAsync(_wim, new ConfigMgrPublishRequest { PackageId = "PS100123", Name = "x" }, cancellationToken: TestContext.Current.CancellationToken);
        site.Calls.Clear();

        await publisher.RollbackAsync("PS100123", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([9], File.ReadAllBytes(source));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(source + ".rejected"));
        Assert.False(File.Exists(source + ".previous"));
        Assert.Equal(["reload PS100123", "refresh PS100123"], site.Calls);
    }

    [Fact]
    public async Task Rollback_WithoutPreviousImage_Fails()
    {
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, Path.Combine(_root, "none.wim"), null) };

        await Assert.ThrowsAsync<ConfigMgrException>(() => new ConfigMgrPublisher(site).RollbackAsync("PS100123", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(site.Calls);
    }

    [Fact]
    public async Task VersionUpdateRejected_IsAWarningNotAFailure()
    {
        var source = Path.Combine(_root, "install.wim");
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null), RejectUpdates = true };

        var result = await new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { PackageId = "PS100123", Name = "x", Version = "2" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("not updated", StringComparison.Ordinal));
        Assert.Contains("refresh PS100123", site.Calls);
    }

    [Fact]
    public async Task UnknownGroup_FailsBeforeAnythingIsCopied()
    {
        var site = new FakeSite();
        var source = Path.Combine(_root, "share", "Win11");

        var ex = await Assert.ThrowsAsync<ConfigMgrException>(() => new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { Name = "x", PackageSourcePath = source, DistributionPointGroups = ["Nope"] },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("All DPs", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(source));
        Assert.Empty(site.Calls);
    }

    [Fact]
    public async Task NewPackageWithoutTargets_Warns()
    {
        var result = await new ConfigMgrPublisher(new FakeSite()).PublishAsync(_wim,
            new ConfigMgrPublishRequest { Name = "x", PackageSourcePath = Path.Combine(_root, "s") }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("not distributed", StringComparison.Ordinal));
    }

    private sealed class FakeSite : IConfigMgrConnection
    {
        public ImagePackage? Existing { get; init; }

        public bool RejectUpdates { get; init; }

        public List<string> Calls { get; } = [];

        public List<string> Targets { get; } = [];

        public ConfigMgrTransport Transport => ConfigMgrTransport.Wmi;

        public string SiteCode => "PS1";

        public Task<IReadOnlyList<ImagePackage>> GetImagePackagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImagePackage>>(Existing is null ? [] : [Existing]);

        public Task<ImagePackage?> GetImagePackageAsync(string packageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Existing?.PackageId == packageId ? Existing : null);

        public Task<string> CreateImagePackageAsync(string name, string? version, string? description, string sourcePath, CancellationToken cancellationToken = default)
        {
            Calls.Add($"create {name} {version} {sourcePath}");
            return Task.FromResult("PS100200");
        }

        public Task UpdateImagePackageAsync(string packageId, string? version, string? description, CancellationToken cancellationToken = default)
        {
            if (RejectUpdates)
            {
                throw new ConfigMgrException("PATCH not supported");
            }

            Calls.Add($"update {packageId} {version}");
            return Task.CompletedTask;
        }

        public Task ReloadImagePropertiesAsync(string packageId, CancellationToken cancellationToken = default) => Record($"reload {packageId}");

        public Task RefreshDistributionPointsAsync(string packageId, CancellationToken cancellationToken = default) => Record($"refresh {packageId}");

        public Task<IReadOnlyList<DistributionPointGroup>> GetDistributionPointGroupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DistributionPointGroup>>([new DistributionPointGroup("g1", "All DPs", 3)]);

        public Task<IReadOnlyList<DistributionPoint>> GetDistributionPointsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DistributionPoint>>(
                [new DistributionPoint("dp01.contoso.com", "PS1", @"[""Display=\\dp01.contoso.com\""]MSWNET:[""SMS_SITE=PS1""]\\dp01.contoso.com\")]);

        public Task<IReadOnlyList<string>> GetPackageTargetsAsync(string packageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Targets);

        public Task DistributeToGroupAsync(string packageId, string groupId, CancellationToken cancellationToken = default) => Record($"group {packageId} {groupId}");

        public Task DistributeToPointAsync(string packageId, DistributionPoint point, CancellationToken cancellationToken = default) =>
            Record($"point {packageId} {point.ServerName}");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }
}
