using Familiar.Core.Updates;

namespace Familiar.Core.Tests.Updates;

public sealed class DownloadManifestTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));

    public DownloadManifestTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Read_MissingManifest_IsEmpty()
    {
        Assert.Empty(DownloadManifest.Read(_folder));
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        DownloadManifest.Write(_folder, ["a.msu", "b.msu"]);

        Assert.Equal(["a.msu", "b.msu"], DownloadManifest.Read(_folder));
    }

    [Fact]
    public void Read_DamagedManifest_IsEmpty()
    {
        File.WriteAllText(Path.Combine(_folder, UpdateDownloader.ManifestFileName), "{ not json");

        Assert.Empty(DownloadManifest.Read(_folder));
    }
}
