using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Familiar.Core.Releases.DataForGeeks;

namespace Familiar.Core.Releases;

/// <summary>The DataForGeeks files Familiar reads.</summary>
public enum ReleaseDataFile
{
    Releases,
    BuildNumbers,
    ClientLifecycle,
    LtscLifecycle,
    ServerLifecycle,
}

/// <summary>Parsed release data, before it is shaped into a <see cref="ReleaseCatalog"/>.</summary>
public sealed class ReleaseDataSet
{
    private ReleaseDataSet(
        IReadOnlyList<ReleaseRecord> releases,
        IReadOnlyList<BuildNumberRecord> buildNumbers,
        IReadOnlyList<ClientLifecycleRecord> clientLifecycle,
        IReadOnlyList<LtscLifecycleRecord> ltscLifecycle,
        IReadOnlyList<ServerLifecycleRecord> serverLifecycle,
        DateTimeOffset? collectedAt)
    {
        Releases = releases;
        BuildNumbers = buildNumbers;
        ClientLifecycle = clientLifecycle;
        LtscLifecycle = ltscLifecycle;
        ServerLifecycle = serverLifecycle;
        CollectedAt = collectedAt;
    }

    internal IReadOnlyList<ReleaseRecord> Releases { get; }
    internal IReadOnlyList<BuildNumberRecord> BuildNumbers { get; }
    internal IReadOnlyList<ClientLifecycleRecord> ClientLifecycle { get; }
    internal IReadOnlyList<LtscLifecycleRecord> LtscLifecycle { get; }
    internal IReadOnlyList<ServerLifecycleRecord> ServerLifecycle { get; }

    /// <summary>
    /// When the update history was collected upstream. It drives release, patch-level and latest-update answers;
    /// the lifecycle files change rarely and are collected on their own schedules.
    /// </summary>
    public DateTimeOffset? CollectedAt { get; }

    public static string FileName(ReleaseDataFile file) => file switch
    {
        ReleaseDataFile.Releases => "releases.json",
        ReleaseDataFile.BuildNumbers => "buildnumbers.json",
        ReleaseDataFile.ClientLifecycle => "lifecycle-client.json",
        ReleaseDataFile.LtscLifecycle => "lifecycle-ltsc.json",
        ReleaseDataFile.ServerLifecycle => "lifecycle-server.json",
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    };

    /// <summary>Parses one stream per file. Throws <see cref="JsonException"/> on malformed data.</summary>
    public static ReleaseDataSet Parse(IReadOnlyDictionary<ReleaseDataFile, Stream> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var context = DataForGeeksJsonContext.Default;
        var releases = Read(files, ReleaseDataFile.Releases, context.EnvelopeReleaseRecord);
        var builds = Read(files, ReleaseDataFile.BuildNumbers, context.EnvelopeBuildNumberRecord);
        var client = Read(files, ReleaseDataFile.ClientLifecycle, context.EnvelopeClientLifecycleRecord);
        var ltsc = Read(files, ReleaseDataFile.LtscLifecycle, context.EnvelopeLtscLifecycleRecord);
        var server = Read(files, ReleaseDataFile.ServerLifecycle, context.EnvelopeServerLifecycleRecord);

        return new ReleaseDataSet(releases.Data, builds.Data, client.Data, ltsc.Data, server.Data,
            builds.Metadata.LastCollected);
    }

    /// <summary>Reads the metadata block of one file without materializing its records.</summary>
    internal static DateTimeOffset? ReadCollectedAt(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("lastCollected", out var collected)
            && collected.TryGetDateTimeOffset(out var value)
                ? value
                : null;
    }

    private static Envelope<T> Read<T>(
        IReadOnlyDictionary<ReleaseDataFile, Stream> files,
        ReleaseDataFile file,
        JsonTypeInfo<Envelope<T>> typeInfo)
    {
        if (!files.TryGetValue(file, out var stream))
        {
            throw new ArgumentException($"Missing release data file {FileName(file)}.", nameof(files));
        }

        return JsonSerializer.Deserialize(stream, typeInfo)
            ?? throw new JsonException($"{FileName(file)} is empty.");
    }
}
