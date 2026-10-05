using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Familiar.Core.Servicing;

/// <summary>Version details read from a mounted image's SOFTWARE hive.</summary>
public sealed record OfflineWindowsVersion(string? DisplayVersion, string? ReleaseId, int? CurrentBuild, int? UpdateBuildRevision, string? EditionId);

public static class OfflineRegistry
{
    /// <summary>
    /// Reads <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c> from a mounted image. This is the reliable
    /// source for releases whose WIM metadata reports the base build (e.g. Windows 10 20H2).
    /// </summary>
    public static OfflineWindowsVersion ReadVersion(string mountPath)
    {
        var hive = Path.Combine(mountPath, "Windows", "System32", "config", "SOFTWARE");
        if (!File.Exists(hive))
        {
            throw new FileNotFoundException("The mounted image has no SOFTWARE hive.", hive);
        }

        // A unique key name avoids colliding with another tool (or another Familiar run) that left a hive loaded.
        var keyName = "Familiar_" + Guid.NewGuid().ToString("N")[..8];
        Reg("load", $@"HKLM\{keyName}", hive);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{keyName}\Microsoft\Windows NT\CurrentVersion");
            return new OfflineWindowsVersion(
                key?.GetValue("DisplayVersion") as string,
                key?.GetValue("ReleaseId") as string,
                ParseInt(key?.GetValue("CurrentBuild")),
                key?.GetValue("UBR") is int ubr ? ubr : null,
                key?.GetValue("EditionID") as string);
        }
        finally
        {
            // Open handles keep the hive loaded; make sure the key above is closed before unloading.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Reg("unload", $@"HKLM\{keyName}");
        }
    }

    private static int? ParseInt(object? value) =>
        value is string s && int.TryParse(s, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static void Reg(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("reg.exe did not start.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"reg {arguments[0]} failed: {error.Trim()}");
        }
    }
}
