using System.Globalization;
using Microsoft.Win32;

namespace Familiar.Core.Servicing;

/// <summary>Version details read from a mounted image's SOFTWARE hive.</summary>
public sealed record OfflineWindowsVersion(string? DisplayVersion, string? ReleaseId, int? CurrentBuild, int? UpdateBuildRevision, string? EditionId);

/// <summary>
/// Reads registry values from hives DISM has loaded for an offline session. DISM loads the image's hives
/// itself when a session opens, so loading them again with <c>reg load</c> fails with a sharing violation.
/// </summary>
public static class OfflineRegistry
{
    private const string CurrentVersionPath = @"Microsoft\Windows NT\CurrentVersion";

    /// <summary>Reads <c>Microsoft\Windows NT\CurrentVersion</c> under the hive DISM mounted at <paramref name="mountPoint"/>.</summary>
    public static OfflineWindowsVersion ReadVersion(string mountPoint)
    {
        var (root, path) = ParseMountPoint(mountPoint);
        using var key = root.OpenSubKey(path + @"\" + CurrentVersionPath)
            ?? throw new InvalidOperationException($"The image's SOFTWARE hive at '{mountPoint}' has no CurrentVersion key.");

        return new OfflineWindowsVersion(
            key.GetValue("DisplayVersion") as string,
            key.GetValue("ReleaseId") as string,
            key.GetValue("CurrentBuild") is string build && int.TryParse(build, CultureInfo.InvariantCulture, out var b) ? b : null,
            key.GetValue("UBR") is int ubr ? ubr : null,
            key.GetValue("EditionID") as string);
    }

    /// <summary>
    /// Splits a registry mount point into a root key and subkey path. Accepts Win32 forms
    /// (<c>HKEY_LOCAL_MACHINE\x</c>, <c>HKLM\x</c>) and native forms (<c>\Registry\Machine\x</c>).
    /// </summary>
    internal static (RegistryKey Root, string Path) ParseMountPoint(string mountPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPoint);
        var value = mountPoint.Trim().TrimEnd('\\');

        (string Prefix, RegistryKey Root)[] roots =
        [
            (@"\Registry\Machine\", Registry.LocalMachine),
            (@"\Registry\User\", Registry.Users),
            (@"HKEY_LOCAL_MACHINE\", Registry.LocalMachine),
            (@"HKLM\", Registry.LocalMachine),
            (@"HKEY_USERS\", Registry.Users),
            (@"HKU\", Registry.Users),
        ];

        foreach (var (prefix, root) in roots)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (root, value[prefix.Length..]);
            }
        }

        // A bare key name is relative to HKLM, where DISM loads offline hives.
        return value.Contains('\\', StringComparison.Ordinal) && !value.StartsWith('\\')
            ? throw new FormatException($"Unrecognized registry mount point '{mountPoint}'.")
            : (Registry.LocalMachine, value.TrimStart('\\'));
    }
}
