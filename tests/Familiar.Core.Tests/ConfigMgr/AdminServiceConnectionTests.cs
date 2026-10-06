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
        var handler = new FakeHandler(r => r.Method == "GET" ? Json("""{ "value": [ { "SiteCode": "PS1" } ] }""") : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        await using var connection = await Connect(handler);

        var ex = await Assert.ThrowsAsync<ConfigMgrException>(() =>
            connection.ReloadImagePropertiesAsync("PS1000B2", TestContext.Current.CancellationToken));

        Assert.Contains("does not support the operation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateImagePackage_IsRefusedWithoutSendingAnything()
    {
        // On 2603, PATCH is not implemented and POST replaces the whole object, so no update request is ever sent.
        var handler = new FakeHandler(_ => Json("""{ "value": [ { "SiteCode": "PS1" } ] }"""));
        await using var connection = await Connect(handler);

        var ex = await Assert.ThrowsAsync<ConfigMgrException>(() =>
            connection.UpdateImagePackageAsync("PS1000B2", null, null, @"\\s\new.wim", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Use transport Auto or Wmi", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UpdateImagePackage_NothingToChange_SendsNothing()
    {
        var handler = new FakeHandler(_ => Json("""{ "value": [ { "SiteCode": "PS1" } ] }"""));
        await using var connection = await Connect(handler);

        await connection.UpdateImagePackageAsync("PS1000B2", null, null, null, cancellationToken: TestContext.Current.CancellationToken);

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
