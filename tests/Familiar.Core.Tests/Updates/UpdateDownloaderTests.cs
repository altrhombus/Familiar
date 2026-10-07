using System.Net;
using System.Security.Cryptography;
using Familiar.Core.Tests.Releases;
using Familiar.Core.Updates;

namespace Familiar.Core.Tests.Updates;

public sealed class UpdateDownloaderTests : IDisposable
{
    private static readonly byte[] Content = [1, 2, 3, 4, 5];
    private static readonly Uri FileUrl = new("https://catalog.sf.dl.delivery.mp.microsoft.com/files/windows11.0-kb5100000-x64.msu");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));
    private readonly string _folder;

    public UpdateDownloaderTests()
    {
        _folder = Path.Combine(_root, "Updates");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Download_WithMatchingDigest_KeepsTheFile()
    {
        var handler = new FileHandler(Content);

        var result = await Downloader(handler).DownloadFileAsync(File("windows11.0-kb5100000-x64.msu", Digest(Content)), _folder, null, TestContext.Current.CancellationToken);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(Content, System.IO.File.ReadAllBytes(result.Path));
        Assert.Equal([Path.Combine(_folder, "windows11.0-kb5100000-x64.msu")], Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_WithWrongDigest_FailsAndKeepsNothing()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(new FileHandler(Content))
            .DownloadFileAsync(File("windows11.0-kb5100000-x64.msu", Digest([9])), _folder, null, TestContext.Current.CancellationToken));

        Assert.Contains("failed verification", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_NotSignedByMicrosoft_FailsAndKeepsNothing()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(new FileHandler(Content), _ => "it has no signature")
            .DownloadFileAsync(File("windows11.0-kb5100000-x64.msu", Digest(Content)), _folder, null, TestContext.Current.CancellationToken));

        Assert.Contains("it has no signature", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public async Task Download_WithoutAUsableDigest_FailsBeforeDownloading(string? digest)
    {
        var handler = new FileHandler(Content);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(handler)
            .DownloadFileAsync(File("windows11.0-kb5100000-x64.msu", digest), _folder, null, TestContext.Current.CancellationToken));

        Assert.Contains("cannot be verified", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_WithoutDigest_DoesNotTrustAFileAlreadyPresent()
    {
        System.IO.File.WriteAllBytes(Path.Combine(_folder, "windows11.0-kb5100000-x64.msu"), [9]);

        await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(new FileHandler(Content))
            .DownloadFileAsync(File("windows11.0-kb5100000-x64.msu", null), _folder, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_ReplacesAFilePresentWithDifferentContent()
    {
        var path = Path.Combine(_folder, "windows11.0-kb5100000-x64.msu");
        System.IO.File.WriteAllBytes(path, [9]);

        var result = await Downloader(new FileHandler(Content)).DownloadFileAsync(File("windows11.0-kb5100000-x64.msu", Digest(Content)), _folder, null, TestContext.Current.CancellationToken);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(Content, System.IO.File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Download_ReusesAFilePresentWithTheRightContent()
    {
        System.IO.File.WriteAllBytes(Path.Combine(_folder, "windows11.0-kb5100000-x64.msu"), Content);
        var handler = new FileHandler(Content);

        var result = await Downloader(handler).DownloadFileAsync(File("windows11.0-kb5100000-x64.msu", Digest(Content)), _folder, null, TestContext.Current.CancellationToken);

        Assert.True(result.AlreadyPresent);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(@"..\outside.msu")]
    [InlineData("../outside.msu")]
    [InlineData(@"sub\inside.msu")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData("C:outside.msu")]
    [InlineData("update.msu:stream")]
    [InlineData("..")]
    [InlineData("")]
    public async Task Download_WithAnUnsafeFileName_FailsBeforeDownloading(string fileName)
    {
        var handler = new FileHandler(Content);

        await Assert.ThrowsAsync<InvalidDataException>(() => Downloader(handler)
            .DownloadFileAsync(File(fileName, Digest(Content)), _folder, null, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Requests);
        Assert.Equal([_folder], Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Prune_DeletesSupersededDownloads_KeepsCurrentAndUnlistedFiles()
    {
        Touch("old-kb5000000.msu", "current-kb5100000.msu", "mine.msu");
        WriteManifest("old-kb5000000.msu", "current-kb5100000.msu");

        var removed = UpdateDownloader.PruneSuperseded(_folder, ["current-kb5100000.msu"]);

        Assert.Equal([Path.Combine(_folder, "old-kb5000000.msu")], removed);
        Assert.True(System.IO.File.Exists(Path.Combine(_folder, "current-kb5100000.msu")));
        Assert.True(System.IO.File.Exists(Path.Combine(_folder, "mine.msu")));
    }

    [Fact]
    public void Prune_IgnoresManifestEntriesThatLeadOutsideTheFolder()
    {
        var outside = Path.Combine(_root, "outside.msu");
        System.IO.File.WriteAllBytes(outside, [1]);
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        var nested = Path.Combine(_folder, "sub", "nested.msu");
        System.IO.File.WriteAllBytes(nested, [1]);
        WriteManifest(@"..\outside.msu", outside, @"sub\nested.msu", "..");

        var removed = UpdateDownloader.PruneSuperseded(_folder, []);

        Assert.Empty(removed);
        Assert.True(System.IO.File.Exists(outside));
        Assert.True(System.IO.File.Exists(nested));
    }

    [Fact]
    public void Prune_OnlyDeletesUpdateFiles()
    {
        Touch("notes.txt", "familiar.exe");
        WriteManifest("notes.txt", "familiar.exe");

        Assert.Empty(UpdateDownloader.PruneSuperseded(_folder, []));
        Assert.True(System.IO.File.Exists(Path.Combine(_folder, "notes.txt")));
        Assert.True(System.IO.File.Exists(Path.Combine(_folder, "familiar.exe")));
    }

    [Theory]
    [InlineData("7TYYeOwrVqff248lZWUmOn/cjqo=", true)]
    [InlineData("7TYYeOwrVqff248lZWUmOn/cjqo", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", false)]
    [InlineData(null, false)]
    public void IsSha1Digest_AcceptsOnlyTwentyByteBase64(string? digest, bool expected)
    {
        Assert.Equal(expected, UpdateDownloader.IsSha1Digest(digest));
    }

    // The content here is a few bytes, not a signed update, so the signature check is replaced; UpdateSignatureTests cover it.
    private static UpdateDownloader Downloader(HttpMessageHandler handler, Func<string, string?>? checkSignature = null) =>
        new(new HttpClient(handler), TestData.Catalog(), checkSignature ?? (_ => null));

    private static CatalogFile File(string fileName, string? digest) => new(FileUrl, fileName, digest);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "The catalog's digest is SHA-1.")]
    private static string Digest(byte[] content) => Convert.ToBase64String(SHA1.HashData(content));

    private void Touch(params string[] names)
    {
        foreach (var name in names)
        {
            System.IO.File.WriteAllBytes(Path.Combine(_folder, name), [1]);
        }
    }

    private void WriteManifest(params string[] entries) =>
        System.IO.File.WriteAllText(
            Path.Combine(_folder, UpdateDownloader.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(entries));

    private sealed class FileHandler(byte[] content) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
