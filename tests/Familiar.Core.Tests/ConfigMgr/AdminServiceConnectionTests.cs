using System.Net;
using System.Text;
using Familiar.Core.ConfigMgr;

namespace Familiar.Core.Tests.ConfigMgr;

public class AdminServiceConnectionTests
{
    [Fact]
    public async Task Connect_ReadsSiteCodeFromSmsSite()
    {
        var handler = new FakeHandler(_ => Json("""{ "value": [ { "SiteCode": "PS1", "Type": 2 } ] }"""));

        await using var connection = await Connect(handler);

        Assert.Equal("PS1", connection.SiteCode);
        Assert.Equal("https://cm01.contoso.com/AdminService/wmi/SMS_Site?$select=SiteCode,Type", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task CreateImagePackage_PostsPropertiesToTheClass()
    {
        var handler = new FakeHandler(r => r.Method == "GET"
            ? Json("""{ "value": [ { "SiteCode": "PS1" } ] }""")
            : Json("""{ "PackageID": "PS100123" }""", HttpStatusCode.Created));
        await using var connection = await Connect(handler);

        var id = await connection.CreateImagePackageAsync("Win11 25H2", "2026.10.05", null, @"\\cm01\Sources$\OSD\install.wim", TestContext.Current.CancellationToken);

        Assert.Equal("PS100123", id);
        var post = handler.Requests[1];
        Assert.Equal(("POST", "https://cm01.contoso.com/AdminService/wmi/SMS_ImagePackage"), (post.Method, post.Uri));
        Assert.Equal("""{"Name":"Win11 25H2","PkgSourcePath":"\\\\cm01\\Sources$\\OSD\\install.wim","Version":"2026.10.05"}""", post.Body);
    }

    [Fact]
    public async Task CreateImagePackage_WithoutResponseBody_FindsItBySourcePath()
    {
        var handler = new FakeHandler(r => r switch
        {
            { Method: "POST" } => new HttpResponseMessage(HttpStatusCode.Created),
            { Uri: var u } when u.Contains("SMS_ImagePackage", StringComparison.Ordinal) =>
                Json("""{ "value": [ { "PackageID": "PS100124", "Name": "x", "PkgSourcePath": "\\\\cm01\\s\\install.wim" } ] }"""),
            _ => Json("""{ "value": [ { "SiteCode": "PS1" } ] }"""),
        });
        await using var connection = await Connect(handler);

        Assert.Equal("PS100124", await connection.CreateImagePackageAsync("x", null, null, @"\\cm01\s\install.wim", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstanceMethods_UseKeyAndAdminServicePrefix()
    {
        var handler = new FakeHandler(r => r.Method == "GET" ? Json("""{ "value": [ { "SiteCode": "PS1" } ] }""") : Json("{}"));
        await using var connection = await Connect(handler);

        await connection.ReloadImagePropertiesAsync("ps100123", TestContext.Current.CancellationToken);
        await connection.DistributeToGroupAsync("PS100123", "{9A2C4B2E-1D3F-4A5B-8C6D-7E8F9A0B1C2D}", TestContext.Current.CancellationToken);

        Assert.Equal("https://cm01.contoso.com/AdminService/wmi/SMS_ImagePackage('PS100123')/AdminService.ReloadImageProperties", handler.Requests[1].Uri);
        Assert.Equal("https://cm01.contoso.com/AdminService/wmi/SMS_DistributionPointGroup('{9A2C4B2E-1D3F-4A5B-8C6D-7E8F9A0B1C2D}')/AdminService.AddPackages", handler.Requests[2].Uri);
        Assert.Equal("""{"PackageIDs":["PS100123"]}""", handler.Requests[2].Body);
    }

    [Fact]
    public async Task UnsupportedOperation_SaysSo()
    {
        var site = new SimulatedSite(PostBehavior.Reject);
        await using var connection = await Connect(new FakeHandler(site.Respond));

        var ex = await Assert.ThrowsAsync<ConfigMgrException>(() =>
            connection.UpdateImagePackageAsync("PS1000B2", "2", null, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not support the operation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateImagePackage_PostsToTheClassWithTheKey()
    {
        var site = new SimulatedSite(PostBehavior.Merge);
        var handler = new FakeHandler(site.Respond);
        await using var connection = await Connect(handler);

        await connection.UpdateImagePackageAsync("ps1000b2", "2026.10.06", null, @"\\s\Images\25H2_AMD64_2610_2\install.wim", TestContext.Current.CancellationToken);

        var update = handler.Requests.Single(r => r.Method == "POST");
        Assert.Equal("https://cm01.contoso.com/AdminService/wmi/SMS_ImagePackage", update.Uri);
        Assert.Equal("""{"Version":"2026.10.06","PkgSourcePath":"\\\\s\\Images\\25H2_AMD64_2610_2\\install.wim","PackageID":"PS1000B2"}""", update.Body);
        Assert.Equal(@"\\s\Images\25H2_AMD64_2610_2\install.wim", site.Packages["PS1000B2"]["PkgSourcePath"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateThatCreatesADuplicate_FailsAndNamesIt()
    {
        var site = new SimulatedSite(PostBehavior.Create);
        await using var connection = await Connect(new FakeHandler(site.Respond));

        var ex = await Assert.ThrowsAsync<ConfigMgrSideEffectException>(() =>
            connection.UpdateImagePackageAsync("PS1000B2", null, null, @"\\s\new.wim", TestContext.Current.CancellationToken));

        Assert.Contains("PS1000B3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("delete it", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateThatBlanksUnsentFields_Fails()
    {
        var site = new SimulatedSite(PostBehavior.Replace);
        await using var connection = await Connect(new FakeHandler(site.Respond));

        var ex = await Assert.ThrowsAsync<ConfigMgrSideEffectException>(() =>
            connection.UpdateImagePackageAsync("PS1000B2", null, null, @"\\s\new.wim", TestContext.Current.CancellationToken));

        Assert.Contains("Name is \"\", expected \"Familiar Test\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Description", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateImagePackage_NothingToChange_SendsNothing()
    {
        var handler = new FakeHandler(_ => Json("""{ "value": [ { "SiteCode": "PS1" } ] }"""));
        await using var connection = await Connect(handler);

        await connection.UpdateImagePackageAsync("PS1000B2", null, null, null, TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MaliciousPackageId_NeverReachesTheUrl()
    {
        var handler = new FakeHandler(_ => Json("""{ "value": [ { "SiteCode": "PS1" } ] }"""));
        await using var connection = await Connect(handler);

        await Assert.ThrowsAsync<ConfigMgrException>(() => connection.GetImagePackageAsync("PS1' or 1 eq 1 or '", TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    private static Task<AdminServiceConnection> Connect(FakeHandler handler) =>
        AdminServiceConnection.ConnectAsync(
            new HttpClient(handler) { BaseAddress = new Uri("https://cm01.contoso.com/AdminService/wmi/") },
            siteCode: null,
            ownsClient: true,
            TestContext.Current.CancellationToken);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private enum PostBehavior
    {
        Merge,
        Create,
        Replace,
        Reject,
    }

    /// <summary>One image package and the ways a site might interpret a POST with its key.</summary>
    private sealed class SimulatedSite(PostBehavior behavior)
    {
        public Dictionary<string, System.Text.Json.Nodes.JsonObject> Packages { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PS1000B2"] = new()
            {
                ["PackageID"] = "PS1000B2",
                ["Name"] = "Familiar Test",
                ["Version"] = "1",
                ["Description"] = "Safe to delete.",
                ["PkgSourcePath"] = @"\\s\old.wim",
            },
        };

        public HttpResponseMessage Respond(Request request)
        {
            if (request.Uri.Contains("SMS_Site", StringComparison.Ordinal))
            {
                return Json("""{ "value": [ { "SiteCode": "PS1" } ] }""");
            }

            if (request.Method == "GET")
            {
                var values = new System.Text.Json.Nodes.JsonArray(Packages.Values.Select(p => (System.Text.Json.Nodes.JsonNode)p.DeepClone()).ToArray());
                return Json(new System.Text.Json.Nodes.JsonObject { ["value"] = values }.ToJsonString());
            }

            var body = System.Text.Json.Nodes.JsonNode.Parse(request.Body!)!.AsObject();
            var id = body["PackageID"]!.GetValue<string>();
            switch (behavior)
            {
                case PostBehavior.Reject:
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                case PostBehavior.Create:
                    var copy = (System.Text.Json.Nodes.JsonObject)body.DeepClone();
                    copy["PackageID"] = "PS1000B3";
                    copy["Name"] = "";
                    Packages["PS1000B3"] = copy;
                    break;
                case PostBehavior.Replace:
                    Packages[id] = (System.Text.Json.Nodes.JsonObject)body.DeepClone();
                    break;
                default:
                    foreach (var (key, value) in body)
                    {
                        Packages[id][key] = value?.DeepClone();
                    }

                    break;
            }

            return Json("{}");
        }
    }

    private sealed record Request(string Method, string Uri, string? Body);

    private sealed class FakeHandler(Func<Request, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new Request(request.Method.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), body);
            Requests.Add(recorded);
            return respond(recorded);
        }
    }
}
