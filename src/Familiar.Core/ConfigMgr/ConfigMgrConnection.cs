namespace Familiar.Core.ConfigMgr;

public enum ConfigMgrTransport
{
    /// <summary>AdminService when it answers, otherwise WMI.</summary>
    Auto,

    /// <summary>The REST administration service on the SMS Provider (HTTPS, Windows authentication).</summary>
    AdminService,

    /// <summary>WMI on the SMS Provider (DCOM). Works on every site; needs RPC access to the provider.</summary>
    Wmi,
}

/// <summary>An OS image package (SMS_ImagePackage).</summary>
public sealed record ImagePackage(
    string PackageId,
    string Name,
    string? Version,
    string? Description,
    string? SourcePath,
    string? ImageOSVersion);

/// <summary>A distribution point group (SMS_DistributionPointGroup).</summary>
public sealed record DistributionPointGroup(string GroupId, string Name, int MemberCount);

/// <summary>A distribution point (SMS_DistributionPointInfo).</summary>
public sealed record DistributionPoint(string ServerName, string SiteCode, string NalPath);

/// <summary>
/// The operations Familiar needs from a Configuration Manager site. Both transports talk to the same SMS Provider
/// classes, so the behavior is identical; only the wire differs.
/// </summary>
public interface IConfigMgrConnection : IAsyncDisposable
{
    ConfigMgrTransport Transport { get; }

    string SiteCode { get; }

    Task<IReadOnlyList<ImagePackage>> GetImagePackagesAsync(CancellationToken cancellationToken = default);

    Task<ImagePackage?> GetImagePackageAsync(string packageId, CancellationToken cancellationToken = default);

    /// <returns>The new PackageID.</returns>
    Task<string> CreateImagePackageAsync(string name, string? version, string? description, string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>Changes properties; null leaves a property as it is.</summary>
    Task UpdateImagePackageAsync(string packageId, string? version, string? description, string? sourcePath = null, CancellationToken cancellationToken = default);

    /// <summary>Re-reads index, OS version and language details from the WIM at the package source.</summary>
    Task ReloadImagePropertiesAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>Redistributes the package source to every distribution point that already has it.</summary>
    Task RefreshDistributionPointsAsync(string packageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DistributionPointGroup>> GetDistributionPointGroupsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DistributionPoint>> GetDistributionPointsAsync(CancellationToken cancellationToken = default);

    /// <summary>NAL paths of the distribution points the package is already targeted to.</summary>
    Task<IReadOnlyList<string>> GetPackageTargetsAsync(string packageId, CancellationToken cancellationToken = default);

    Task DistributeToGroupAsync(string packageId, string groupId, CancellationToken cancellationToken = default);

    Task DistributeToPointAsync(string packageId, DistributionPoint point, CancellationToken cancellationToken = default);
}

/// <summary>A site rejected or could not complete a request.</summary>
public class ConfigMgrException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A request changed more than intended (for example created a duplicate package). Never retried another way:
/// the site needs a person to look at it first.
/// </summary>
public sealed class ConfigMgrSideEffectException(string message) : ConfigMgrException(message);
