using System.Globalization;
using Familiar.Core.Releases.DataForGeeks;

namespace Familiar.Core.Releases;

/// <summary>
/// Windows releases, cumulative updates and lifecycles, derived from DataForGeeks data rather than a
/// hard-coded table, so new feature updates need a data refresh instead of a Familiar release.
/// </summary>
public sealed class ReleaseCatalog
{
    // Windows 11 builds start at 22000; anything lower on the client side is Windows 10.
    private const int FirstWindows11Build = 22000;

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M/d/yyyy"];

    private readonly ReleaseDataSet _data;

    private ReleaseCatalog(ReleaseDataSet data, IReadOnlyList<WindowsRelease> releases, IReadOnlyList<WindowsUpdate> updates)
    {
        _data = data;
        Releases = releases;
        Updates = updates;
    }

    public IReadOnlyList<WindowsRelease> Releases { get; }

    public IReadOnlyList<WindowsUpdate> Updates { get; }

    public DateTimeOffset? CollectedAt => _data.CollectedAt;

    public static ReleaseCatalog Create(ReleaseDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var updates = data.BuildNumbers.Select(ToUpdate).OfType<WindowsUpdate>().ToList();
        var baselines = ComputeBaselines(updates);

        // An explicit base_build in releases.json wins over inference, which needs a shared KB to exist first.
        foreach (var record in data.Releases)
        {
            if (TryParseOsType(record.OsType, out var osType) && int.TryParse(record.BaseBuild, CultureInfo.InvariantCulture, out var baseBuild))
            {
                baselines[new ReleaseKey(osType, record.MajorVersion, StripQualifier(record.WindowsVersion))] = baseBuild;
            }
        }

        var releases = new Dictionary<ReleaseKey, WindowsRelease>();
        foreach (var record in data.Releases)
        {
            if (TryParseOsType(record.OsType, out var osType) && int.TryParse(record.OsBuild, CultureInfo.InvariantCulture, out var build))
            {
                Add(new ReleaseKey(osType, record.MajorVersion, StripQualifier(record.WindowsVersion)), build);
            }
        }

        // releases.json covers client only; Server (and any client release not listed yet) comes from update history.
        foreach (var update in updates)
        {
            Add(new ReleaseKey(update.OsType, update.MajorVersion, update.WindowsVersion), update.Build);
        }

        var ordered = releases.Values
            .OrderBy(r => r.OsType)
            .ThenBy(r => r.Build)
            .ThenBy(r => r.MajorVersion)
            .ToList();

        return new ReleaseCatalog(data, ordered, updates);

        void Add(ReleaseKey key, int build)
        {
            if (!releases.ContainsKey(key))
            {
                releases[key] = new WindowsRelease(key.OsType, key.MajorVersion, key.Version, build, baselines.GetValueOrDefault(key, build));
            }
        }
    }

    /// <summary>Resolves the release for an image.</summary>
    /// <param name="build">Major build from the image metadata.</param>
    /// <param name="installationType">DISM InstallationType: "Client", "Server", "Server Core", ...</param>
    /// <param name="displayVersion">
    /// DisplayVersion (or ReleaseId) from the mounted image's registry. Pass it when available: media for
    /// enablement releases can report the base build in its metadata, and only the registry tells them apart.
    /// </param>
    /// <returns>The release, or <c>null</c> when the data does not know the build.</returns>
    public WindowsRelease? Resolve(int build, string? installationType, string? displayVersion = null)
    {
        var osType = installationType?.StartsWith("Server", StringComparison.OrdinalIgnoreCase) == true
            ? WindowsOsType.Server
            : WindowsOsType.Client;

        var candidates = Releases.Where(r => r.OsType == osType).ToList();

        if (osType == WindowsOsType.Client && !string.IsNullOrWhiteSpace(displayVersion))
        {
            var major = build >= FirstWindows11Build ? 11 : 10;
            var version = NormalizeDisplayVersion(displayVersion);
            var byVersion = candidates.FirstOrDefault(r =>
                r.MajorVersion == major && string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));
            if (byVersion is not null)
            {
                return byVersion;
            }
        }

        return candidates.FirstOrDefault(r => r.Build == build);
    }

    /// <summary>The cumulative update that produced an image's build, if the data lists it.</summary>
    public WindowsUpdate? FindInstalledUpdate(WindowsRelease release, int revision)
    {
        ArgumentNullException.ThrowIfNull(release);

        // Media for a new enablement release is often built from a CU published before that release existed
        // (25H2 shipped at 26200.6584, from KB5065426, which lists only 26100.6584). Releases on one branch share
        // revisions, so fall back to the same revision anywhere on the branch.
        return UpdatesFor(release).FirstOrDefault(u => u.Build == release.Build && u.Revision == revision)
            ?? Updates.FirstOrDefault(u =>
                u.OsType == release.OsType
                && u.Revision == revision
                && Releases.Any(r => r.OsType == u.OsType
                    && r.MajorVersion == u.MajorVersion
                    && string.Equals(r.Version, u.WindowsVersion, StringComparison.OrdinalIgnoreCase)
                    && r.ServicingBaselineBuild == release.ServicingBaselineBuild));
    }

    /// <summary>The newest cumulative update that can be slipstreamed into the release.</summary>
    public WindowsUpdate? GetLatestUpdate(WindowsRelease release, bool includePreview = false)
    {
        ArgumentNullException.ThrowIfNull(release);

        return UpdatesFor(release)
            .Where(u => u.CanSlipstream && (includePreview || u.Type != WindowsUpdateType.Preview))
            .OrderByDescending(u => u.Revision)
            .ThenByDescending(u => u.ReleaseDate)
            .FirstOrDefault();
    }

    /// <summary>The support lifecycle for a release and DISM EditionId, if published.</summary>
    public SupportLifecycle? GetLifecycle(WindowsRelease release, string? editionId)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (release.OsType == WindowsOsType.Server)
        {
            var serverName = $"Windows Server {release.MajorVersion}";
            var server = _data.ServerLifecycle.FirstOrDefault(r =>
                string.Equals(r.Version.Trim(), serverName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Tier.Trim(), serverName, StringComparison.OrdinalIgnoreCase));
            return server is null ? null : ToLifecycle(server.Tier, server.StartDate, server.ExtendedEndDate);
        }

        if (IsLtsc(editionId))
        {
            var ltsc = _data.LtscLifecycle.FirstOrDefault(r => r.Build.Trim() == release.Build.ToString(CultureInfo.InvariantCulture));
            return ltsc is null ? null : ToLifecycle($"{ltsc.ServicingOption.Trim()} {ltsc.Version.Trim()}", ltsc.StartDate, ltsc.ExtendedEndDate);
        }

        var sku = $"Windows {release.MajorVersion} {(IsEnterpriseLifecycle(editionId) ? "Enterprise and Education" : "Home and Pro")}";
        var client = _data.ClientLifecycle.FirstOrDefault(r =>
            string.Equals(r.Version.Trim(), release.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Sku.Trim(), sku, StringComparison.OrdinalIgnoreCase));
        return client is null ? null : ToLifecycle(client.Sku, client.StartDate, client.EndDate);
    }

    private IEnumerable<WindowsUpdate> UpdatesFor(WindowsRelease release) =>
        Updates.Where(u =>
            u.OsType == release.OsType
            && u.MajorVersion == release.MajorVersion
            && string.Equals(u.WindowsVersion, release.Version, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Groups releases into servicing branches. Two releases are on one branch when the same KB produced the
    /// same revision on both (enablement releases such as 24H2/25H2 move in lockstep). Matching on revision,
    /// not just KB, keeps one mislabeled KB from merging unrelated branches. Client and Server are kept apart:
    /// they can share KBs but never share language media.
    /// </summary>
    private static Dictionary<ReleaseKey, int> ComputeBaselines(IReadOnlyList<WindowsUpdate> updates)
    {
        var parent = new Dictionary<ReleaseKey, ReleaseKey>();
        var builds = new Dictionary<ReleaseKey, int>();

        foreach (var update in updates)
        {
            var key = KeyOf(update);
            parent.TryAdd(key, key);
            builds[key] = Math.Min(builds.GetValueOrDefault(key, int.MaxValue), update.Build);
        }

        var lockstep = updates.GroupBy(u => (u.OsType, Kb: u.KbArticle.ToUpperInvariant(), u.Revision));
        foreach (var group in lockstep)
        {
            var keys = group.Select(KeyOf).Distinct().ToList();
            for (var i = 1; i < keys.Count; i++)
            {
                Union(keys[0], keys[i]);
            }
        }

        var baselineByRoot = new Dictionary<ReleaseKey, int>();
        foreach (var key in parent.Keys)
        {
            var root = Find(key);
            baselineByRoot[root] = Math.Min(baselineByRoot.GetValueOrDefault(root, int.MaxValue), builds[key]);
        }

        return parent.Keys.ToDictionary(k => k, k => baselineByRoot[Find(k)]);

        ReleaseKey Find(ReleaseKey key)
        {
            while (parent[key] != key)
            {
                parent[key] = parent[parent[key]];
                key = parent[key];
            }

            return key;
        }

        void Union(ReleaseKey a, ReleaseKey b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB)
            {
                parent[rootB] = rootA;
            }
        }

        static ReleaseKey KeyOf(WindowsUpdate u) => new(u.OsType, u.MajorVersion, u.WindowsVersion);
    }

    private static WindowsUpdate? ToUpdate(BuildNumberRecord record)
    {
        var parts = record.Build.Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var build)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var revision)
            || !TryParseOsType(record.OsType, out var osType)
            || !TryParseDate(record.ReleaseDate, out var date))
        {
            return null;
        }

        var type = record.ReleaseType.Trim().ToUpperInvariant() switch
        {
            "STANDARD" => WindowsUpdateType.Standard,
            "PREVIEW" => WindowsUpdateType.Preview,
            "OUT-OF-BAND" => WindowsUpdateType.OutOfBand,
            "HOTPATCH" => WindowsUpdateType.Hotpatch,
            "HOTPATCH-OOB" => WindowsUpdateType.HotpatchOutOfBand,
            _ => WindowsUpdateType.Unknown,
        };

        Uri.TryCreate(record.ArticleUrl, UriKind.Absolute, out var url);

        return new WindowsUpdate(osType, record.MajorVersion, record.WindowsVersion.Trim(), build, revision,
            record.KbArticle.Trim(), date, type, record.IsExpired, url);
    }

    private static SupportLifecycle? ToLifecycle(string channel, string start, string end) =>
        TryParseDate(start, out var startDate)
            ? new SupportLifecycle(channel.Trim(), startDate, TryParseDate(end, out var endDate) ? endDate : null)
            : null;

    // Lifecycle dates arrive as "2024-10-01", "11/1/2024 " or, from scraped footnotes, "8/2/2016 8".
    internal static bool TryParseDate(string? value, out DateOnly date)
    {
        var token = value?.Trim().Split(' ', 2)[0];
        return DateOnly.TryParseExact(token, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryParseOsType(string value, out WindowsOsType osType)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "CLIENT":
                osType = WindowsOsType.Client;
                return true;
            case "SERVER":
                osType = WindowsOsType.Server;
                return true;
            default:
                osType = default;
                return false;
        }
    }

    // "1507 (RTM)" -> "1507"
    private static string StripQualifier(string version)
    {
        var space = version.IndexOf(' ', StringComparison.Ordinal);
        return (space < 0 ? version : version[..space]).Trim();
    }

    // Windows 10 20H2 reports ReleaseId "2009" alongside DisplayVersion "20H2".
    private static string NormalizeDisplayVersion(string value) =>
        value.Trim() == "2009" ? "20H2" : value.Trim();

    private static bool IsLtsc(string? editionId) =>
        editionId is not null && editionId.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnterpriseLifecycle(string? editionId) =>
        editionId is not null
        && (editionId.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
            || editionId.StartsWith("Education", StringComparison.OrdinalIgnoreCase)
            || editionId.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase));

    private readonly record struct ReleaseKey(WindowsOsType OsType, int MajorVersion, string Version)
    {
        public bool Equals(ReleaseKey other) =>
            OsType == other.OsType && MajorVersion == other.MajorVersion
            && string.Equals(Version, other.Version, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(OsType, MajorVersion, StringComparer.OrdinalIgnoreCase.GetHashCode(Version));
    }
}
