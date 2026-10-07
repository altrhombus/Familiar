using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;

namespace Familiar.Core.ConfigMgr;

/// <summary>
/// Talks to the SMS Provider over WMI (DCOM) using the current Windows identity. This is what the ConfigMgr
/// console and PowerShell module do underneath, so it works on every site that allows remote WMI.
/// </summary>
public sealed class WmiConnection : IConfigMgrConnection
{
    private const string Wql = "WQL";

    private readonly CimSession _session;
    private readonly string _siteServer;
    private readonly string _namespace;

    private WmiConnection(CimSession session, string siteServer, string siteCode)
    {
        _session = session;
        _siteServer = siteServer;
        SiteCode = siteCode;
        _namespace = $@"root\SMS\site_{siteCode}";
    }

    public ConfigMgrTransport Transport => ConfigMgrTransport.Wmi;

    public string SiteCode { get; }

    public static Task<WmiConnection> ConnectAsync(string siteServer, string? siteCode, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var session = CimSession.Create(siteServer, new DComSessionOptions { Timeout = TimeSpan.FromSeconds(30) });
            try
            {
                // The provider's own site, unless the caller named one.
                var code = siteCode ?? session
                    .QueryInstances(@"root\SMS", Wql, "SELECT SiteCode FROM SMS_ProviderLocation WHERE ProviderForLocalSite = TRUE")
                    .Select(i => i.CimInstanceProperties["SiteCode"]?.Value as string)
                    .FirstOrDefault(c => !string.IsNullOrEmpty(c))
                    ?? throw new ConfigMgrException($"{siteServer} does not host an SMS Provider.");

                return new WmiConnection(session, siteServer, ConfigMgrIds.SiteCode(code));
            }
            catch (CimException ex)
            {
                session.Dispose();
                throw new ConfigMgrException($"Could not reach the SMS Provider on {siteServer} over WMI: {ex.Message}", ex);
            }
        }, cancellationToken);

    public Task<IReadOnlyList<ImagePackage>> GetImagePackagesAsync(CancellationToken cancellationToken = default) =>
        Query("SELECT * FROM SMS_ImagePackage", ToImagePackage, cancellationToken);

    public async Task<ImagePackage?> GetImagePackageAsync(string packageId, CancellationToken cancellationToken = default) =>
        await Query($"SELECT * FROM SMS_ImagePackage WHERE PackageID = '{ConfigMgrIds.Package(packageId)}'", ToImagePackage, cancellationToken).ConfigureAwait(false)
            is [var package, ..] ? package : null;

    public Task<string> CreateImagePackageAsync(string name, string? version, string? description, string sourcePath, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            using var instance = new CimInstance("SMS_ImagePackage", _namespace);
            Set(instance, "Name", name);
            Set(instance, "PkgSourcePath", sourcePath);
            Set(instance, "Version", version);
            Set(instance, "Description", description);

            using var created = _session.CreateInstance(_namespace, instance);
            return created.CimInstanceProperties["PackageID"]?.Value as string
                ?? throw new ConfigMgrException("The site created the image package but did not return its PackageID.");
        }, cancellationToken);

    /// <remarks>
    /// Uses System.Management rather than MI: get the whole object by key, change it, and put the whole object
    /// back, the pattern the ConfigMgr SDK uses. An MI ModifyInstance over DCOM was accepted by a real site
    /// without the new PkgSourcePath taking effect.
    /// </remarks>
    public Task UpdateImagePackageAsync(string packageId, string? version, string? description, string? sourcePath = null, string? name = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                var scope = new System.Management.ManagementScope($@"\\{_siteServer}\{_namespace}");
                scope.Connect();
                using var package = new System.Management.ManagementObject(
                    scope, new System.Management.ManagementPath($"SMS_ImagePackage.PackageID='{ConfigMgrIds.Package(packageId)}'"), null);
                package.Get();

                if (version is not null)
                {
                    package["Version"] = version;
                }

                if (description is not null)
                {
                    package["Description"] = description;
                }

                if (sourcePath is not null)
                {
                    package["PkgSourcePath"] = sourcePath;
                }

                if (name is not null)
                {
                    package["Name"] = name;
                }

                package.Put();
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                throw new ConfigMgrException($"WMI update of {packageId} failed: {ex.Message}", ex);
            }
        }, cancellationToken);

    public Task ReloadImagePropertiesAsync(string packageId, CancellationToken cancellationToken = default) =>
        InvokeOnPackage(packageId, "ReloadImageProperties", cancellationToken);

    public Task RefreshDistributionPointsAsync(string packageId, CancellationToken cancellationToken = default) =>
        InvokeOnPackage(packageId, "RefreshPkgSource", cancellationToken);

    public Task<IReadOnlyList<DistributionPointGroup>> GetDistributionPointGroupsAsync(CancellationToken cancellationToken = default) =>
        Query("SELECT GroupID, Name, MemberCount FROM SMS_DistributionPointGroup",
            i => new DistributionPointGroup(String(i, "GroupID")!, String(i, "Name")!, Convert.ToInt32(i.CimInstanceProperties["MemberCount"]?.Value ?? 0, System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);

    public Task<IReadOnlyList<DistributionPoint>> GetDistributionPointsAsync(CancellationToken cancellationToken = default) =>
        Query("SELECT ServerName, SiteCode, NALPath FROM SMS_DistributionPointInfo",
            i => new DistributionPoint(String(i, "ServerName")!, String(i, "SiteCode")!, String(i, "NALPath")!),
            cancellationToken);

    public Task<IReadOnlyList<string>> GetPackageTargetsAsync(string packageId, CancellationToken cancellationToken = default) =>
        Query($"SELECT ServerNALPath FROM SMS_DistributionPoint WHERE PackageID = '{ConfigMgrIds.Package(packageId)}'",
            i => String(i, "ServerNALPath")!, cancellationToken);

    public Task DistributeToGroupAsync(string packageId, string groupId, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            using var group = Single($"SELECT * FROM SMS_DistributionPointGroup WHERE GroupID = '{ConfigMgrIds.Group(groupId)}'");
            using var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("PackageIDs", new[] { ConfigMgrIds.Package(packageId) }, CimType.StringArray, CimFlags.In),
            };
            Check(_session.InvokeMethod(_namespace, group, "AddPackages", parameters), "AddPackages");
            return true;
        }, cancellationToken);

    public Task DistributeToPointAsync(string packageId, DistributionPoint point, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            using var instance = new CimInstance("SMS_DistributionPoint", _namespace);
            Set(instance, "PackageID", ConfigMgrIds.Package(packageId));
            Set(instance, "ServerNALPath", point.NalPath);
            Set(instance, "SiteCode", point.SiteCode);
            using var created = _session.CreateInstance(_namespace, instance);
            return true;
        }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<bool> InvokeOnPackage(string packageId, string method, CancellationToken cancellationToken) =>
        Run(() =>
        {
            using var instance = Single($"SELECT * FROM SMS_ImagePackage WHERE PackageID = '{ConfigMgrIds.Package(packageId)}'");
            Check(_session.InvokeMethod(_namespace, instance, method, new CimMethodParametersCollection()), method);
            return true;
        }, cancellationToken);

    private CimInstance Single(string query) =>
        _session.QueryInstances(_namespace, Wql, query).FirstOrDefault()
        ?? throw new ConfigMgrException($"Not found: {query}");

    private Task<IReadOnlyList<T>> Query<T>(string query, Func<CimInstance, T> map, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<T>>(() =>
        {
            var results = new List<T>();
            foreach (var instance in _session.QueryInstances(_namespace, Wql, query))
            {
                using (instance)
                {
                    results.Add(map(instance));
                }
            }

            return results;
        }, cancellationToken);

    // MI calls are synchronous; run them off the caller's thread and turn CIM failures into ConfigMgrException.
    private static Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ex is CimException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                throw new ConfigMgrException($"WMI request failed: {ex.Message}", ex);
            }
        }, cancellationToken);

    private static void Check(CimMethodResult result, string method)
    {
        if (result.ReturnValue?.Value is { } value && Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            throw new ConfigMgrException($"{method} returned {value}.");
        }
    }

    private static void Set(CimInstance instance, string name, string? value)
    {
        if (value is not null)
        {
            instance.CimInstanceProperties.Add(CimProperty.Create(name, value, CimType.String, CimFlags.None));
        }
    }

    private static string? String(CimInstance instance, string name) => instance.CimInstanceProperties[name]?.Value as string;

    private static ImagePackage ToImagePackage(CimInstance i) => new(
        String(i, "PackageID")!, String(i, "Name") ?? string.Empty, String(i, "Version"), String(i, "Description"),
        String(i, "PkgSourcePath"), String(i, "ImageOSVersion"),
        i.CimInstanceProperties["SourceVersion"]?.Value is { } sourceVersion ? Convert.ToInt64(sourceVersion, System.Globalization.CultureInfo.InvariantCulture) : null);
}
