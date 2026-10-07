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
    public async Task InPlace_SecondPublish_KeepsTheEarlierImageToo()
    {
        var source = InPlaceSource([9]);
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null) };
        var publisher = new ConfigMgrPublisher(site);
        await publisher.PublishAsync(_wim, new ConfigMgrPublishRequest { PackageId = "PS100123" }, cancellationToken: TestContext.Current.CancellationToken);

        var result = await publisher.PublishAsync(Wim("second.wim", [5]), new ConfigMgrPublishRequest { PackageId = "PS100123" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([5], File.ReadAllBytes(source));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(source + ".previous"));
        Assert.Equal([9], File.ReadAllBytes(source + ".previous.1"));
        Assert.Contains(result.Warnings, w => w.Contains("1 earlier image(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InPlace_RepeatedRollbacks_WalkBackThroughEveryKeptImage()
    {
        var source = InPlaceSource([9]);
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null) };
        var publisher = new ConfigMgrPublisher(site);
        await publisher.PublishAsync(_wim, new ConfigMgrPublishRequest { PackageId = "PS100123" }, cancellationToken: TestContext.Current.CancellationToken);
        await publisher.PublishAsync(Wim("second.wim", [5]), new ConfigMgrPublishRequest { PackageId = "PS100123" }, cancellationToken: TestContext.Current.CancellationToken);
        await publisher.PublishAsync(Wim("third.wim", [6]), new ConfigMgrPublishRequest { PackageId = "PS100123" }, cancellationToken: TestContext.Current.CancellationToken);

        await publisher.RollbackAsync("PS100123", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([5], File.ReadAllBytes(source));

        await publisher.RollbackAsync("PS100123", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(source));

        await publisher.RollbackAsync("PS100123", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([9], File.ReadAllBytes(source));
        Assert.Empty(ConfigMgrPublisher.EarlierImagePaths(source));
        await Assert.ThrowsAsync<ConfigMgrException>(() => publisher.RollbackAsync("PS100123", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InPlace_CancelledCopy_LeavesSourceAndKeptImageUntouched()
    {
        var source = InPlaceSource([9]);
        File.WriteAllBytes(source + ".previous", [7]);
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null) };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { PackageId = "PS100123" }, cancellationToken: cancelled.Token));

        Assert.Equal([9], File.ReadAllBytes(source));
        Assert.Equal([7], File.ReadAllBytes(source + ".previous"));
        Assert.Equal([source, source + ".previous"], Directory.GetFiles(Path.GetDirectoryName(source)!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InPlace_SourceInUse_FailsWithoutChangingAnything()
    {
        var source = InPlaceSource([9]);
        File.WriteAllBytes(source + ".previous", [7]);
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null) };

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => new ConfigMgrPublisher(site).PublishAsync(_wim,
                new ConfigMgrPublishRequest { PackageId = "PS100123" }, cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Equal([9], File.ReadAllBytes(source));
        Assert.Equal([7], File.ReadAllBytes(source + ".previous"));
        Assert.Equal([source, source + ".previous"], Directory.GetFiles(Path.GetDirectoryName(source)!).Order(StringComparer.Ordinal));
        Assert.Empty(site.Calls);
    }

    [Fact]
    public async Task Rollback_WhenThePreviousImageCannotBeRestored_PutsTheCurrentOneBack()
    {
        var source = InPlaceSource([5]);
        File.WriteAllBytes(source + ".previous", [9]);
        var site = new FakeSite { Existing = new ImagePackage("PS100123", "Win11", "1", null, source, null) };

        using (new FileStream(source + ".previous", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => new ConfigMgrPublisher(site).RollbackAsync("PS100123", cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Equal([5], File.ReadAllBytes(source));
        Assert.Equal([9], File.ReadAllBytes(source + ".previous"));
        Assert.False(File.Exists(source + ".rejected"));
        Assert.Empty(site.Calls);
    }

    [Fact]
    public async Task NewPackage_WhenTheTargetFileExists_FailsWithoutReplacingIt()
    {
        var source = Path.Combine(_root, "share", "Win11");
        Directory.CreateDirectory(source);
        var other = Path.Combine(source, "install-25h2.wim");
        File.WriteAllBytes(other, [9]);
        var site = new FakeSite();

        var ex = await Assert.ThrowsAsync<ConfigMgrException>(() => new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { Name = "x", PackageSourcePath = source }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("sourceFolderName", ex.Message, StringComparison.Ordinal);
        Assert.Equal([9], File.ReadAllBytes(other));
        Assert.Single(Directory.GetFiles(source));
        Assert.Empty(site.Calls);
    }

    private string InPlaceSource(byte[] content)
    {
        var source = Path.Combine(_root, "share", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, content);
        return source;
    }

    private string Wim(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task VersionedFolders_UpdateCopiesBesideAndRepointsThePackage()
    {
        var images = Path.Combine(_root, "Images", "Windows 11");
        var current = Path.Combine(images, "25H2_AMD64_2601_1", "Windows11-25H2-Enterprise.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllBytes(current, [9]);
        var site = new FakeSite { Existing = new ImagePackage("PS1000A1", "Windows 11 25H2 Enterprise AMD64", "1", null, current, null) };

        var result = await new ConfigMgrPublisher(site).PublishAsync(_wim, new ConfigMgrPublishRequest
        {
            PackageId = "PS1000A1",
            
            Version = "2026.10.06",
            SourceFolderName = "{release}_{arch}_{date:yyMM}_{n}",
            Image = new ImageFacts("25H2", "AMD64", "Enterprise", "26200.9457", "KB5129195", new DateOnly(2026, 9, 14)),
        }, cancellationToken: TestContext.Current.CancellationToken);

        var expected = Path.Combine(images, $"25H2_AMD64_{DateTime.Now:yyMM}_1", "Windows11-25H2-Enterprise.wim");
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(expected));
        Assert.Equal([9], File.ReadAllBytes(current));
        Assert.Equal(current, result.PreviousImagePath);
        Assert.Equal(["update PS1000A1 2026.10.06 source=" + expected, "reload PS1000A1", "refresh PS1000A1"], site.Calls);
        Assert.False(File.Exists(current + ".previous"));
    }

    [Fact]
    public async Task VersionedFolders_RollbackRepointsAtTheReplacedFolder()
    {
        var images = Path.Combine(_root, "Images");
        var current = Path.Combine(images, "25H2_AMD64_2601_1", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllBytes(current, [9]);
        var site = new FakeSite { Existing = new ImagePackage("PS1000A1", "x", "1", null, current, null) };
        var publisher = new ConfigMgrPublisher(site);
        await publisher.PublishAsync(_wim, new ConfigMgrPublishRequest { PackageId = "PS1000A1", Name = "x", SourceFolderName = "{date:yyMMdd}.{n}" },
            cancellationToken: TestContext.Current.CancellationToken);
        var published = site.Existing.SourcePath!;
        site.Calls.Clear();

        await publisher.RollbackAsync("PS1000A1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(current, site.Existing.SourcePath);
        Assert.True(File.Exists(published), "the rolled-back folder is left in place");
        Assert.Equal(["update PS1000A1  source=" + current, "reload PS1000A1", "refresh PS1000A1"], site.Calls);
    }

    [Fact]
    public async Task VersionedFolders_FailedRepoint_SaysWhereTheCopyWent()
    {
        var current = Path.Combine(_root, "Images", "a", "install.wim");
        var site = new FakeSite { Existing = new ImagePackage("PS1000A1", "x", "1", null, current, null), RejectUpdates = true };

        var ex = await Assert.ThrowsAsync<ConfigMgrException>(() => new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { PackageId = "PS1000A1", Name = "x", SourceFolderName = "b.{n}" },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("still points at", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reload PS1000A1", site.Calls);
    }

    [Fact]
    public async Task VersionedFolders_UpdateAcceptedButNotApplied_FailsInsteadOfReportingSuccess()
    {
        // What a real site did: the update call succeeded, yet PkgSourcePath did not change.
        var current = Path.Combine(_root, "Images", "25H2_AMD64_2610_1", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllBytes(current, [9]);
        var site = new FakeSite { Existing = new ImagePackage("PS1000B2", "x", "1", null, current, null), IgnoreSourceUpdates = true };

        var ex = await Assert.ThrowsAsync<ConfigMgrException>(() => new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { PackageId = "PS1000B2", Name = "x", SourceFolderName = "25H2_AMD64_2610_{n}" },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("still points at", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reload PS1000B2", site.Calls);
        Assert.DoesNotContain("refresh PS1000B2", site.Calls);
    }

    [Fact]
    public async Task VersionedFolders_NewPackageGoesIntoAFolderUnderTheSourcePath()
    {
        var site = new FakeSite();
        var root = Path.Combine(_root, "Familiar-Test");

        await new ConfigMgrPublisher(site).PublishAsync(_wim,
            new ConfigMgrPublishRequest { Name = "Familiar Test", PackageSourcePath = root, SourceFolderName = "{release}_{arch}_{n}", Image = new ImageFacts("25H2", "AMD64", "Enterprise", "26200.9457", null, null) },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(root, "25H2_AMD64_1", "install-25h2.wim")));
    }

    [Theory]
    [InlineData(false, "still serve the previous content")]
    [InlineData(true, "content version changed from 7 to 8")]
    public async Task WithoutRefresh_ChecksTheSiteDidNotRedistributeAnyway(bool redistributes, string expected)
    {
        var current = Path.Combine(_root, "Images", "25H2_AMD64_2601_1", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllBytes(current, [9]);
        var site = new FakeSite
        {
            Existing = new ImagePackage("PS1000A1", "Windows 11 25H2 Enterprise AMD64", null, null, current, null, SourceVersion: 7),
            RedistributesOnChange = redistributes,
        };

        var result = await new ConfigMgrPublisher(site).PublishAsync(_wim, new ConfigMgrPublishRequest
        {
            PackageId = "PS1000A1",
            Name = "x",
            SourceFolderName = "25H2_AMD64_2610_{n}",
            RefreshDistributionPoints = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains(expected, StringComparison.Ordinal));
        Assert.DoesNotContain("refresh PS1000A1", site.Calls);
    }

    [Fact]
    public async Task OlderImage_IsRefusedBeforeAnythingIsCopied()
    {
        var current = Path.Combine(_root, "Images", "25H2_AMD64_2601_1", "Windows11-25H2-Enterprise.wim");
        var site = new FakeSite { Existing = new ImagePackage("PS1000A1", "x", null, null, current, "10.0.26200.7628") };

        var ex = await Assert.ThrowsAsync<ConfigMgrException>(() => new ConfigMgrPublisher(site).PublishAsync(_wim, new ConfigMgrPublishRequest
        {
            PackageId = "PS1000A1",
            SourceFolderName = "25H2_AMD64_2610_{n}",
            Image = new ImageFacts("25H2", "AMD64", "Enterprise", "26200.6584", "KB5065426", new DateOnly(2025, 9, 9)),
        }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("older than the 26200.7628", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "Images", "25H2_AMD64_2610_1")));
        Assert.Empty(site.Calls);
    }

    [Fact]
    public async Task OlderImage_AllowedWhenAskedFor()
    {
        var current = Path.Combine(_root, "Images", "25H2_AMD64_2601_1", "install.wim");
        var site = new FakeSite { Existing = new ImagePackage("PS1000A1", "x", null, null, current, "10.0.26200.7628") };

        await new ConfigMgrPublisher(site).PublishAsync(_wim, new ConfigMgrPublishRequest
        {
            PackageId = "PS1000A1",
            SourceFolderName = "25H2_AMD64_2610_{n}",
            Image = new ImageFacts("25H2", "AMD64", "Enterprise", "26200.6584", null, null),
            AllowOlderImage = true,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("reload PS1000A1", site.Calls);
    }

    [Fact]
    public async Task ExistingPackage_RenamedOnlyWhenNameIsSet()
    {
        var current = Path.Combine(_root, "Images", "a", "install.wim");
        var site = new FakeSite { Existing = new ImagePackage("PS1000A1", "Windows 11 25H2 Enterprise AMD64", null, null, current, null) };

        await new ConfigMgrPublisher(site).PublishAsync(_wim, new ConfigMgrPublishRequest
        {
            PackageId = "PS1000A1",
            Name = "Windows 11 AMD64",
            Version = "25H2",
            Description = "2026-09",
            SourceFolderName = "b.{n}",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(site.Calls, c => c.StartsWith("update PS1000A1 25H2", StringComparison.Ordinal) && c.EndsWith("name=Windows 11 AMD64", StringComparison.Ordinal));
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
        public ImagePackage? Existing { get; set; }

        public bool RejectUpdates { get; init; }

        /// <summary>Simulate a site that starts redistributing when the package changes.</summary>
        public bool RedistributesOnChange { get; init; }

        /// <summary>Accept source path updates without applying them.</summary>
        public bool IgnoreSourceUpdates { get; init; }

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

        public Task UpdateImagePackageAsync(string packageId, string? version, string? description, string? sourcePath = null, string? name = null, CancellationToken cancellationToken = default)
        {
            if (RejectUpdates)
            {
                throw new ConfigMgrException("PATCH not supported");
            }

            Calls.Add((sourcePath is null ? $"update {packageId} {version}" : $"update {packageId} {version} source={sourcePath}")
                + (name is null ? string.Empty : $" name={name}"));
            if (sourcePath is not null && Existing is not null && !IgnoreSourceUpdates)
            {
                Existing = Existing with { SourcePath = sourcePath };
            }

            return Task.CompletedTask;
        }

        public Task ReloadImagePropertiesAsync(string packageId, CancellationToken cancellationToken = default)
        {
            if (RedistributesOnChange && Existing is not null)
            {
                Existing = Existing with { SourceVersion = (Existing.SourceVersion ?? 0) + 1 };
            }

            return Record($"reload {packageId}");
        }

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
