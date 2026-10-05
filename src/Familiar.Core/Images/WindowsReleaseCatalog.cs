namespace Familiar.Core.Images;

/// <summary>Maps image build numbers to known Windows releases.</summary>
public static class WindowsReleaseCatalog
{
    public static IReadOnlyList<WindowsRelease> All { get; } =
    [
        new(WindowsFamily.Windows10, "Windows 10 1607", 14393, "1607", "1607"),
        new(WindowsFamily.Windows10, "Windows 10 1809", 17763, "1809", "1809"),
        new(WindowsFamily.Windows10, "Windows 10 1903", 18362, "1903", "1903"),
        new(WindowsFamily.Windows10, "Windows 10 1909", 18363, "1909", "1903"),
        new(WindowsFamily.Windows10, "Windows 10 2004", 19041, "2004", "2004"),
        new(WindowsFamily.Windows10, "Windows 10 20H2", 19042, "20H2", "2004"),
        new(WindowsFamily.Windows10, "Windows 10 21H1", 19043, "21H1", "2004"),
        new(WindowsFamily.Windows10, "Windows 10 21H2", 19044, "21H2", "2004"),
        new(WindowsFamily.Windows10, "Windows 10 22H2", 19045, "22H2", "2004"),

        new(WindowsFamily.Windows11, "Windows 11 21H2", 22000, "21H2", "21H2"),
        new(WindowsFamily.Windows11, "Windows 11 22H2", 22621, "22H2", "22H2"),
        new(WindowsFamily.Windows11, "Windows 11 23H2", 22631, "23H2", "22H2"),
        new(WindowsFamily.Windows11, "Windows 11 24H2", 26100, "24H2", "24H2"),
        new(WindowsFamily.Windows11, "Windows 11 25H2", 26200, "25H2", "24H2"),

        new(WindowsFamily.WindowsServer, "Windows Server 2016", 14393, "1607", "2016"),
        new(WindowsFamily.WindowsServer, "Windows Server 2019", 17763, "1809", "2019"),
        new(WindowsFamily.WindowsServer, "Windows Server 2022", 20348, "21H2", "2022"),
        new(WindowsFamily.WindowsServer, "Windows Server 2025", 26100, "24H2", "2025"),
    ];

    /// <summary>Resolves the release for an image.</summary>
    /// <param name="build">Major build from the image metadata.</param>
    /// <param name="installationType">DISM InstallationType, e.g. "Client", "Server", "Server Core".</param>
    /// <param name="displayVersion">
    /// DisplayVersion (or ReleaseId) read from the mounted image's registry. Pass it when available:
    /// releases delivered by enablement package (e.g. Windows 10 20H2) can ship media whose metadata
    /// still reports the base build, and only the registry tells them apart.
    /// </param>
    /// <returns>The matching release, or <c>null</c> if the build is unknown.</returns>
    public static WindowsRelease? Resolve(int build, string? installationType, string? displayVersion = null)
    {
        var family = GetFamily(build, installationType);
        var candidates = All.Where(r => r.Family == family);

        if (!string.IsNullOrWhiteSpace(displayVersion))
        {
            var normalized = NormalizeDisplayVersion(displayVersion);
            var byVersion = candidates.FirstOrDefault(r =>
                string.Equals(r.DisplayVersion, normalized, StringComparison.OrdinalIgnoreCase));
            if (byVersion is not null)
            {
                return byVersion;
            }
        }

        return candidates.FirstOrDefault(r => r.Build == build);
    }

    internal static WindowsFamily GetFamily(int build, string? installationType)
    {
        if (installationType?.StartsWith("Server", StringComparison.OrdinalIgnoreCase) == true)
        {
            return WindowsFamily.WindowsServer;
        }

        return build >= 22000 ? WindowsFamily.Windows11 : WindowsFamily.Windows10;
    }

    // Windows 10 20H2 reports ReleaseId "2009" alongside DisplayVersion "20H2".
    private static string NormalizeDisplayVersion(string value) =>
        value.Trim() == "2009" ? "20H2" : value.Trim();
}
