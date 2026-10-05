namespace Familiar.Core.Images;

public enum WindowsFamily
{
    Windows10,
    Windows11,
    WindowsServer,
}

/// <summary>A Windows release that Familiar knows how to service.</summary>
/// <param name="Family">Client generation or Server.</param>
/// <param name="Name">Display name, e.g. "Windows 11 24H2" or "Windows Server 2022".</param>
/// <param name="Build">Major build number, e.g. 26100.</param>
/// <param name="DisplayVersion">
/// The version string the image's registry reports (DisplayVersion, or ReleaseId on older releases).
/// </param>
/// <param name="LanguageMediaVersion">
/// The release whose Language Pack / Features on Demand media applies. Several releases share
/// media, e.g. Windows 10 20H2 through 22H2 all use the 2004 media.
/// </param>
public sealed record WindowsRelease(
    WindowsFamily Family,
    string Name,
    int Build,
    string DisplayVersion,
    string LanguageMediaVersion);
