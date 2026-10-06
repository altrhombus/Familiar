using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Familiar.Core.ConfigMgr;

/// <summary>
/// Talks to the SMS Provider through the administration service's WMI route
/// (<c>https://provider/AdminService/wmi/</c>) with Windows authentication. Microsoft documents GET and POST on
/// this route, with queries and static methods as examples. Creating instances (POST to the class) and instance
/// methods (<c>Class('key')/AdminService.Method</c>) are not documented but work on 2603; property updates are a
/// POST with the key in the body and fall back to WMI if the site rejects them.
/// </summary>
public sealed class AdminServiceConnection : IConfigMgrConnection
{
    // WMI property name on the wire, not this class's SiteCode property.
    private const string SiteCodeProperty = "SiteCode";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    private AdminServiceConnection(HttpClient http, bool ownsClient, string siteCode)
    {
        _http = http;
        _ownsClient = ownsClient;
        SiteCode = siteCode;
    }

    public ConfigMgrTransport Transport => ConfigMgrTransport.AdminService;

    public string SiteCode { get; }

    /// <summary>Creates a client that authenticates as the current Windows user.</summary>
    public static HttpClient CreateClient(string siteServer) =>
        new(new HttpClientHandler { UseDefaultCredentials = true, PreAuthenticate = true })
        {
            BaseAddress = new Uri($"https://{siteServer}/AdminService/wmi/"),
            Timeout = TimeSpan.FromMinutes(2),
        };

    /// <summary>Connects and confirms the service answers, so <see cref="ConfigMgrTransport.Auto"/> can fall back early.</summary>
    /// <param name="http">A client whose BaseAddress is the wmi route; see <see cref="CreateClient"/>.</param>
    public static async Task<AdminServiceConnection> ConnectAsync(HttpClient http, string? siteCode, bool ownsClient = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            var sites = await GetValuesAsync(http, "SMS_Site?$select=SiteCode,Type", cancellationToken).ConfigureAwait(false);
            var code = siteCode
                ?? sites.Select(s => s?[SiteCodeProperty]?.GetValue<string>()).FirstOrDefault(c => !string.IsNullOrEmpty(c))
                ?? throw new ConfigMgrException("The administration service returned no sites.");
            return new AdminServiceConnection(http, ownsClient, ConfigMgrIds.SiteCode(code));
        }
        catch
        {
            if (ownsClient)
            {
                http.Dispose();
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<ImagePackage>> GetImagePackagesAsync(CancellationToken cancellationToken = default) =>
        (await GetValuesAsync(_http, "SMS_ImagePackage", cancellationToken).ConfigureAwait(false)).Select(ToImagePackage).ToList();

    public async Task<ImagePackage?> GetImagePackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var path = $"SMS_ImagePackage?$filter=PackageID eq '{ConfigMgrIds.Package(packageId)}'";
        return (await GetValuesAsync(_http, path, cancellationToken).ConfigureAwait(false)) is [var package, ..] ? ToImagePackage(package) : null;
    }

    public async Task<string> CreateImagePackageAsync(string name, string? version, string? description, string sourcePath, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["Name"] = name, ["PkgSourcePath"] = sourcePath };
        if (version is not null)
        {
            body["Version"] = version;
        }

        if (description is not null)
        {
            body["Description"] = description;
        }

        var created = await SendAsync(HttpMethod.Post, "SMS_ImagePackage", body, cancellationToken).ConfigureAwait(false);
        var id = FirstValue(created)?["PackageID"]?.GetValue<string>();
        if (id is not null)
        {
            return id;
        }

        // Some service versions answer 201 without a body; find the package by its unique source path.
        var match = (await GetImagePackagesAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => string.Equals(p.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
        return match?.PackageId ?? throw new ConfigMgrException("The site accepted the new image package but it could not be found afterwards.");
    }

    /// <remarks>
    /// The WMI route only supports GET and POST; PATCH returns NotImplemented (seen on 2603). An update is a POST to
    /// the class with the key property in the body, which the provider treats as a put of the existing instance.
    /// That form is community-documented rather than Microsoft-documented, so callers verify the result and fall
    /// back to WMI.
    /// </remarks>
    public async Task UpdateImagePackageAsync(string packageId, string? version, string? description, string? sourcePath = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        if (version is not null)
        {
            body["Version"] = version;
        }

        if (description is not null)
        {
            body["Description"] = description;
        }

        if (sourcePath is not null)
        {
            body["PkgSourcePath"] = sourcePath;
        }

        if (body.Count == 0)
        {
            return;
        }

        var id = ConfigMgrIds.Package(packageId);
        var before = await GetImagePackageAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ConfigMgrException($"OS image package {id} was not found.");
        var idsBefore = (await GetImagePackagesAsync(cancellationToken).ConfigureAwait(false)).Select(p => p.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        body["PackageID"] = id;
        await SendAsync(HttpMethod.Post, "SMS_ImagePackage", body, cancellationToken).ConfigureAwait(false);

        // The POST form is undocumented: it could have created a new package or replaced the object, blanking
        // fields that were not sent. Check for both before anyone relies on it.
        var after = await GetImagePackagesAsync(cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();

        var added = after.Where(p => !idsBefore.Contains(p.PackageId)).ToList();
        if (added.Count > 0)
        {
            problems.Add($"a new image package appeared ({string.Join(", ", added.Select(p => $"{p.PackageId} \"{p.Name}\""))}); if it duplicates {id}, delete it in the console");
        }

        var updated = after.FirstOrDefault(p => string.Equals(p.PackageId, id, StringComparison.OrdinalIgnoreCase));
        if (updated is null)
        {
            problems.Add($"{id} is no longer listed");
        }
        else
        {
            Compare("Name", before.Name, updated.Name, requested: null);
            Compare("Version", before.Version, updated.Version, version);
            Compare("Description", before.Description, updated.Description, description);
        }

        if (problems.Count > 0)
        {
            throw new ConfigMgrSideEffectException($"The administration service update of {id} had side effects: {string.Join("; ", problems)}. Fix these in the console before publishing again.");
        }

        void Compare(string property, string? previous, string? current, string? requested)
        {
            var expected = requested ?? previous;
            if (!string.Equals(expected ?? string.Empty, current ?? string.Empty, StringComparison.Ordinal))
            {
                problems.Add($"{property} is \"{current}\", expected \"{expected}\"");
            }
        }
    }

    public Task ReloadImagePropertiesAsync(string packageId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"SMS_ImagePackage('{ConfigMgrIds.Package(packageId)}')/AdminService.ReloadImageProperties", new JsonObject(), cancellationToken);

    public Task RefreshDistributionPointsAsync(string packageId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"SMS_ImagePackage('{ConfigMgrIds.Package(packageId)}')/AdminService.RefreshPkgSource", new JsonObject(), cancellationToken);

    public async Task<IReadOnlyList<DistributionPointGroup>> GetDistributionPointGroupsAsync(CancellationToken cancellationToken = default) =>
        (await GetValuesAsync(_http, "SMS_DistributionPointGroup?$select=GroupID,Name,MemberCount", cancellationToken).ConfigureAwait(false))
        .Select(g => new DistributionPointGroup(Text(g, "GroupID")!, Text(g, "Name")!, g?["MemberCount"]?.GetValue<int>() ?? 0))
        .ToList();

    public async Task<IReadOnlyList<DistributionPoint>> GetDistributionPointsAsync(CancellationToken cancellationToken = default) =>
        (await GetValuesAsync(_http, "SMS_DistributionPointInfo?$select=ServerName,SiteCode,NALPath", cancellationToken).ConfigureAwait(false))
        .Select(d => new DistributionPoint(Text(d, "ServerName")!, Text(d, SiteCodeProperty)!, Text(d, "NALPath")!))
        .ToList();

    public async Task<IReadOnlyList<string>> GetPackageTargetsAsync(string packageId, CancellationToken cancellationToken = default) =>
        (await GetValuesAsync(_http, $"SMS_DistributionPoint?$filter=PackageID eq '{ConfigMgrIds.Package(packageId)}'&$select=ServerNALPath", cancellationToken).ConfigureAwait(false))
        .Select(d => Text(d, "ServerNALPath")!)
        .ToList();

    public Task DistributeToGroupAsync(string packageId, string groupId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"SMS_DistributionPointGroup('{ConfigMgrIds.Group(groupId)}')/AdminService.AddPackages",
            new JsonObject { ["PackageIDs"] = new JsonArray(ConfigMgrIds.Package(packageId)) }, cancellationToken);

    public Task DistributeToPointAsync(string packageId, DistributionPoint point, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "SMS_DistributionPoint",
            new JsonObject { ["PackageID"] = ConfigMgrIds.Package(packageId), ["ServerNALPath"] = point.NalPath, [SiteCodeProperty] = point.SiteCode },
            cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"{method} {path}", cancellationToken).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static async Task<IReadOnlyList<JsonNode?>> GetValuesAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"GET {path}", cancellationToken).ConfigureAwait(false);
        var node = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        return node?["value"] is JsonArray values ? [.. values] : node is null ? [] : [node];
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string request, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "access denied; check your ConfigMgr role permissions",
            HttpStatusCode.NotFound => "not found",
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => "this site's administration service does not support the operation",
            _ => $"HTTP {(int)response.StatusCode}",
        };

        throw new ConfigMgrException($"AdminService {request}: {message}. {Truncate(detail)}".Trim(), new HttpRequestException(message, null, response.StatusCode));
    }

    private static JsonNode? FirstValue(JsonNode? node) => node?["value"] is JsonArray { Count: > 0 } values ? values[0] : node;

    private static string? Text(JsonNode? node, string name) => node?[name]?.GetValue<string>();

    private static ImagePackage ToImagePackage(JsonNode? node) => new(
        Text(node, "PackageID")!, Text(node, "Name") ?? string.Empty, Text(node, "Version"), Text(node, "Description"),
        Text(node, "PkgSourcePath"), Text(node, "ImageOSVersion"));

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";
}
