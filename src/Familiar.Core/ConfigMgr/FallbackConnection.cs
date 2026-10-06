namespace Familiar.Core.ConfigMgr;

/// <summary>
/// The administration service with WMI held in reserve for property updates, the one operation whose
/// AdminService form is not documented. WMI is only opened if an update is rejected.
/// </summary>
internal sealed class FallbackConnection(IConfigMgrConnection primary, Func<CancellationToken, Task<IConfigMgrConnection>> openFallback, Action<string> log)
    : IConfigMgrConnection
{
    private IConfigMgrConnection? _fallback;

    public ConfigMgrTransport Transport => primary.Transport;

    public string SiteCode => primary.SiteCode;

    public async Task UpdateImagePackageAsync(string packageId, string? version, string? description, string? sourcePath = null, CancellationToken cancellationToken = default)
    {
        string reason;
        try
        {
            await primary.UpdateImagePackageAsync(packageId, version, description, sourcePath, cancellationToken).ConfigureAwait(false);

            // The update form is undocumented, so an accepted request is not proof; check the one change that matters.
            var actual = sourcePath is null ? null : (await primary.GetImagePackageAsync(packageId, cancellationToken).ConfigureAwait(false))?.SourcePath;
            if (sourcePath is null || string.Equals(actual, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            reason = $"it accepted the update but the package still points at {actual}";
        }
        catch (ConfigMgrException ex) when (ex is not ConfigMgrSideEffectException)
        {
            reason = $"it rejected the update: {ex.Message}";
        }

        log($"The administration service did not update {packageId} ({reason}); retrying over WMI.");
        _fallback ??= await openFallback(cancellationToken).ConfigureAwait(false);
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
