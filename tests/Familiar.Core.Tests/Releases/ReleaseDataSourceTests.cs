using System.Net;
using System.Net.Http.Headers;
using Familiar.Core.Releases;

namespace Familiar.Core.Tests.Releases;

public sealed class ReleaseDataSourceTests : IDisposable
{
    // Newer than the bundled snapshot, so cached copies are preferred over it.
    private const string FutureCollected = "2030-01-01T00:00:00Z";

    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NetworkFailure_WithoutCache_FallsBackToSnapshot()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("offline"));

        var result = await Source(handler).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseDataOrigin.Snapshot, result.Origin);
        Assert.NotEmpty(result.Catalog.Releases);
        Assert.Equal(5, result.Warnings.Count);
    }

    [Fact]
    public async Task SuccessfulDownload_IsCached_AndReusedUntilStale()
    {
        var handler = new FakeHandler(_ => Json(TestData.Envelope("", FutureCollected), "\"v1\""));

        var first = await Source(handler).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var second = await Source(handler).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseDataOrigin.Network, first.Origin);
        Assert.Equal(ReleaseDataOrigin.Cache, second.Origin);
        Assert.Equal(5, handler.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_cacheDirectory, "buildnumbers.json.etag")));
    }

    [Fact]
    public async Task ForcedRefresh_SendsEtag_AndAccepts304()
    {
        var handler = new FakeHandler(_ => Json(TestData.Envelope("", FutureCollected), "\"v1\""));
        await Source(handler).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        var result = await Source(handler).LoadAsync(forceRefresh: true, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseDataOrigin.Network, result.Origin);
        Assert.Empty(result.Warnings);
        Assert.Contains(handler.Requests.Skip(5), r => r.Headers.IfNoneMatch.Contains(new EntityTagHeaderValue("\"v1\"")));
    }

    [Fact]
    public async Task InvalidDownload_DoesNotReplaceCache()
    {
        var handler = new FakeHandler(_ => Json(TestData.Envelope("", FutureCollected)));
        await Source(handler).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var before = await File.ReadAllTextAsync(Path.Combine(_cacheDirectory, "releases.json"), TestContext.Current.CancellationToken);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>rate limited</html>") };
        var result = await Source(handler).LoadAsync(forceRefresh: true, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseDataOrigin.Cache, result.Origin);
        Assert.Equal(5, result.Warnings.Count);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(_cacheDirectory, "releases.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CacheOlderThanSnapshot_UsesSnapshot()
    {
        var handler = new FakeHandler(_ => Json(TestData.Envelope("", "2001-01-01T00:00:00Z")));

        var result = await Source(handler).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseDataOrigin.Snapshot, result.Origin);
    }

    [Fact]
    public async Task NetworkDisabled_NeverCallsOut()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("should not be called"));

        var result = await Source(handler, allowNetwork: false).LoadAsync(forceRefresh: true, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseDataOrigin.Snapshot, result.Origin);
        Assert.Empty(handler.Requests);
        Assert.Empty(result.Warnings);
    }

    private ReleaseDataSource Source(FakeHandler handler, bool allowNetwork = true) =>
        new(new HttpClient(handler), new ReleaseDataSourceOptions
        {
            BaseUri = new Uri("https://example.test/data/"),
            CacheDirectory = _cacheDirectory,
            AllowNetwork = allowNetwork,
        });

    private static HttpResponseMessage Json(MemoryStream body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body.ToArray()) };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }
}
