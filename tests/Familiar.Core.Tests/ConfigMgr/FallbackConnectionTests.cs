using Familiar.Core.ConfigMgr;

namespace Familiar.Core.Tests.ConfigMgr;

public class FallbackConnectionTests
{
    [Fact]
    public async Task RejectedUpdate_IsRetriedOverTheFallback_OpenedOnce()
    {
        var primary = new Recorder(rejectUpdates: true);
        var fallback = new Recorder(rejectUpdates: false);
        var opened = 0;
        var log = new List<string>();
        await using var connection = new FallbackConnection(primary, _ => { opened++; return Task.FromResult<IConfigMgrConnection>(fallback); }, log.Add);

        await connection.UpdateImagePackageAsync("PS1000A1", "1", null, @"\\s\a.wim", TestContext.Current.CancellationToken);
        await connection.UpdateImagePackageAsync("PS1000A1", "2", null, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, opened);
        Assert.Equal(["update PS1000A1 1 \\\\s\\a.wim", "update PS1000A1 2 "], fallback.Calls);
        Assert.Contains(log, l => l.Contains("retrying over WMI", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateAcceptedButNotApplied_FallsBackToo()
    {
        var primary = new Recorder(rejectUpdates: false) { IgnoreSource = true };
        var fallback = new Recorder(rejectUpdates: false);
        var log = new List<string>();
        await using var connection = new FallbackConnection(primary, _ => Task.FromResult<IConfigMgrConnection>(fallback), log.Add);

        await connection.UpdateImagePackageAsync("PS1000B2", null, null, @"\\s\new.wim", TestContext.Current.CancellationToken);

        Assert.Equal(["update PS1000B2  \\\\s\\new.wim"], fallback.Calls);
        Assert.Contains(log, l => l.Contains("still points at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherOperations_StayOnThePrimary()
    {
        var primary = new Recorder(rejectUpdates: false);
        await using var connection = new FallbackConnection(primary, _ => throw new InvalidOperationException("should not open"), _ => { });

        await connection.ReloadImagePropertiesAsync("PS1000A1", TestContext.Current.CancellationToken);
        await connection.UpdateImagePackageAsync("PS1000A1", "1", null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["reload PS1000A1", "update PS1000A1 1 "], primary.Calls);
    }

    private sealed class Recorder(bool rejectUpdates) : IConfigMgrConnection
    {
        public List<string> Calls { get; } = [];

        /// <summary>Accept updates without applying the source path, like a site ignoring an unsupported form.</summary>
        public bool IgnoreSource { get; init; }

        private string? _source;

        public ConfigMgrTransport Transport => ConfigMgrTransport.AdminService;

        public string SiteCode => "PS1";

        public Task UpdateImagePackageAsync(string packageId, string? version, string? description, string? sourcePath = null, CancellationToken cancellationToken = default)
        {
            if (rejectUpdates)
            {
                throw new ConfigMgrException("HTTP 405");
            }

            Calls.Add($"update {packageId} {version} {sourcePath}");
            if (!IgnoreSource)
            {
                _source = sourcePath ?? _source;
            }

            return Task.CompletedTask;
        }

        public Task ReloadImagePropertiesAsync(string packageId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"reload {packageId}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ImagePackage>> GetImagePackagesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ImagePackage?> GetImagePackageAsync(string packageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ImagePackage?>(new ImagePackage(packageId, "x", null, null, _source ?? @"\s\old.wim", null));

        public Task<string> CreateImagePackageAsync(string name, string? version, string? description, string sourcePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RefreshDistributionPointsAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DistributionPointGroup>> GetDistributionPointGroupsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DistributionPoint>> GetDistributionPointsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetPackageTargetsAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DistributeToGroupAsync(string packageId, string groupId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DistributeToPointAsync(string packageId, DistributionPoint point, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
