namespace Familiar.Core.ConfigMgr;

/// <summary>
/// The administration service for everything it supports, and WMI for property updates, which the administration
/// service cannot do (tested on 2603: PATCH is not implemented and POST replaces the whole object). WMI is opened
/// only when an update is needed.
/// </summary>
internal sealed class FallbackConnection(IConfigMgrConnection primary, Func<CancellationToken, Task<IConfigMgrConnection>> openFallback, Action<string> log)
    : IConfigMgrConnection
{
    private IConfigMgrConnection? _fallback;

    public ConfigMgrTransport Transport => primary.Transport;

    public string SiteCode => primary.SiteCode;

    public async Task UpdateImagePackageAsync(string packageId, string? version, string? description, string? sourcePath = null, CancellationToken cancellationToken = default)
    {
        if (version is null && description is null && sourcePath is null)
        {
            return;
        }

        if (_fallback is null)
        {
            log("Updating package properties over WMI (the administration service cannot update properties).");
            _fallback = await openFallback(cancellationToken).ConfigureAwait(false);
        }

        await _fallback.UpdateImagePackageAsync(packageId, version, description, sourcePath, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ImagePackage>> GetImagePackagesAsync(CancellationToken cancellationToken = default) => primary.GetImagePackagesAsync(cancellationToken);

    public Task<ImagePackage?> GetImagePackageAsync(string packageId, CancellationToken cancellationToken = default) => primary.GetImagePackageAsync(packageId, cancellationToken);

    public Task<string> CreateImagePackageAsync(string name, string? version, string? description, string sourcePath, CancellationToken cancellationToken = default) =>
        primary.CreateImagePackageAsync(name, version, description, sourcePath, cancellationToken);

    public Task ReloadImagePropertiesAsync(string packageId, CancellationToken cancellationToken = default) => primary.ReloadImagePropertiesAsync(packageId, cancellationToken);

    public Task RefreshDistributionPointsAsync(string packageId, CancellationToken cancellationToken = default) => primary.RefreshDistributionPointsAsync(packageId, cancellationToken);

    public Task<IReadOnlyList<DistributionPointGroup>> GetDistributionPointGroupsAsync(CancellationToken cancellationToken = default) => primary.GetDistributionPointGroupsAsync(cancellationToken);

    public Task<IReadOnlyList<DistributionPoint>> GetDistributionPointsAsync(CancellationToken cancellationToken = default) => primary.GetDistributionPointsAsync(cancellationToken);

    public Task<IReadOnlyList<string>> GetPackageTargetsAsync(string packageId, CancellationToken cancellationToken = default) => primary.GetPackageTargetsAsync(packageId, cancellationToken);

    public Task DistributeToGroupAsync(string packageId, string groupId, CancellationToken cancellationToken = default) => primary.DistributeToGroupAsync(packageId, groupId, cancellationToken);

    public Task DistributeToPointAsync(string packageId, DistributionPoint point, CancellationToken cancellationToken = default) => primary.DistributeToPointAsync(packageId, point, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await primary.DisposeAsync().ConfigureAwait(false);
        if (_fallback is not null)
        {
            await _fallback.DisposeAsync().ConfigureAwait(false);
        }
    }
}
