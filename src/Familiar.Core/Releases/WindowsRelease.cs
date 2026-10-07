namespace Familiar.Core.Releases;

public enum WindowsOsType
{
    Client,
    Server,
}

/// <summary>A Windows feature release, e.g. Windows 11 25H2 or Windows Server 2025.</summary>
/// <param name="OsType">Client or Server.</param>
/// <param name="MajorVersion">10 or 11 for client; the year (2016, 2022, ...) for Server.</param>
/// <param name="Version">Feature version as published, e.g. "25H2", "1809", "2025".</param>
/// <param name="Build">Major build number, e.g. 26200.</param>
/// <param name="ServicingBaselineBuild">
/// Lowest build on the same servicing branch. Releases on one branch receive the same cumulative updates
/// and share Language Pack and Features on Demand media; e.g. 25H2 (26200) has baseline 26100.
/// </param>
public sealed record WindowsRelease(
    WindowsOsType OsType,
    int MajorVersion,
    string Version,
    int Build,
    int ServicingBaselineBuild)
{
    public string Name => OsType switch
    {
        WindowsOsType.Client => $"Windows {MajorVersion} {Version}",
        _ when IsHalfYearVersion(Version) => $"Windows Server, version {Version}",
        _ => $"Windows Server {MajorVersion}",
    };

    /// <summary>True when this release is an enablement release on top of an earlier build.</summary>
    public bool SharesBaseline => ServicingBaselineBuild != Build;

    public override string ToString() => Name;

    private static bool IsHalfYearVersion(string version) =>
        version.Length == 4 && char.IsAsciiDigit(version[0]) && char.IsAsciiDigit(version[1])
        && version[2] is 'H' or 'h' && char.IsAsciiDigit(version[3]);
}

public enum WindowsUpdateType
{
    Standard,
    Preview,
    OutOfBand,
    Hotpatch,
    HotpatchOutOfBand,
    Unknown,
}

/// <summary>A cumulative update and the build it produces.</summary>
public sealed record WindowsUpdate(
    WindowsOsType OsType,
    int MajorVersion,
    string WindowsVersion,
    int Build,
    int Revision,
    string KbArticle,
    DateOnly ReleaseDate,
    WindowsUpdateType Type,
    bool IsExpired,
    Uri? ArticleUrl)
{
    /// <summary>Hotpatches need a baseline already installed and cannot be slipstreamed into an image.</summary>
    public bool CanSlipstream => Type is WindowsUpdateType.Standard or WindowsUpdateType.Preview or WindowsUpdateType.OutOfBand;

    public override string ToString() => $"{KbArticle} ({Build}.{Revision}, {ReleaseDate:yyyy-MM-dd})";
}

/// <summary>The support window that applies to an image's release and edition.</summary>
/// <param name="Channel">What the dates describe, e.g. "Windows 11 Enterprise and Education".</param>
/// <param name="EndDate">End of security updates; <c>null</c> when the data does not say.</param>
public sealed record SupportLifecycle(string Channel, DateOnly StartDate, DateOnly? EndDate)
{
    public bool IsSupportedOn(DateOnly date) => date >= StartDate && (EndDate is null || date <= EndDate);
}
