using System.Text;
using Familiar.Core.Releases;

namespace Familiar.Core.Tests.Releases;

internal static class TestData
{
    public static ReleaseCatalog Catalog(
        string releases = "",
        string builds = "",
        string clientLifecycle = "",
        string ltscLifecycle = "",
        string serverLifecycle = "")
    {
        var files = new Dictionary<ReleaseDataFile, Stream>
        {
            [ReleaseDataFile.Releases] = Envelope(releases),
            [ReleaseDataFile.BuildNumbers] = Envelope(builds),
            [ReleaseDataFile.ClientLifecycle] = Envelope(clientLifecycle),
            [ReleaseDataFile.LtscLifecycle] = Envelope(ltscLifecycle),
            [ReleaseDataFile.ServerLifecycle] = Envelope(serverLifecycle),
        };

        return ReleaseCatalog.Create(ReleaseDataSet.Parse(files));
    }

    public static MemoryStream Envelope(string records, string collected = "2026-09-29T22:07:13Z") =>
        new(Encoding.UTF8.GetBytes(
            $$"""{ "metadata": { "dataset": "test", "apiVersion": "v2", "recordCount": 0, "lastCollected": "{{collected}}" }, "data": [ {{records}} ] }"""));
}
