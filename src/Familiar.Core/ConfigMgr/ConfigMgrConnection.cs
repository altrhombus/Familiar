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
/// <param name="SourceVersion">
/// Content version at the site; it increments when content is pushed to distribution points, so comparing it
/// before and after a change shows whether redistribution started.
/// </param>
public sealed record ImagePackage(
    string PackageId,
    string Name,
    string? Version,
    string? Description,
    string? SourcePath,
    string? ImageOSVersion,
    long? SourceVersion = null);

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
    Task UpdateImagePackageAsync(string packageId, string? version, string? description, string? sourcePath = null, string? name = null, CancellationToken cancellationToken = default);

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
public sealed class ConfigMgrException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Recognizes failures talking to a site, so callers can report them instead of crashing.</summary>
public static class ConfigMgrErrors
{
    /// <summary>
    /// Whether <paramref name="exception"/> is a site, network or file share failure rather than a bug: Familiar's own
    /// errors, the administration service's transport errors and timeouts, WMI and DCOM errors, and file access.
    /// A cancellation the user asked for is not one.
    /// </summary>
    public static bool IsSiteFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        ConfigMgrException or HttpRequestException or System.Text.Json.JsonException
            or Microsoft.Management.Infrastructure.CimException or System.Management.ManagementException
            or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException => true,
        _ => false,
    };

    /// <summary>A one-line description of a failure <see cref="IsSiteFailure"/> accepts.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            // HttpClient reports its timeout as a cancellation; the site may still be doing the work.
            OperationCanceledException => "the site did not answer in time. It may still be finishing the request, so check the package in the console before retrying.",
            HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "the administration service's HTTPS certificate is not trusted by this machine.",
            _ => exception.Message,
        };
    }
}
