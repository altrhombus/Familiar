using Microsoft.Dism;

namespace Familiar.Core.Images;

/// <summary>
/// Owns the DISM API lifetime for the process. Create one per host (CLI run, app session) and
/// dispose it on exit; DISM does not reliably support re-initializing after shutdown.
/// </summary>
public sealed class DismHost : IDisposable
{
    private bool _disposed;

    /// <param name="logFilePath">Optional DISM log, with warnings and errors.</param>
    public DismHost(string? logFilePath = null)
    {
        if (!ProcessElevation.IsElevated)
        {
            throw new UnauthorizedAccessException("The DISM API requires administrator rights.");
        }

        if (logFilePath is null)
        {
            DismApi.Initialize(DismLogLevel.LogErrors);
            return;
        }

        // DISM fails with DISMAPI_E_LOGGING_DISABLED rather than creating the log's folder.
        var fullPath = Path.GetFullPath(logFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        DismApi.Initialize(DismLogLevel.LogErrorsWarnings, fullPath);
    }

    /// <summary>Lists the indexes in a WIM or ESD file.</summary>
    public IReadOnlyList<ImageIndexInfo> GetIndexes(string imagePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Servicing.DismImageServicer.ReadIndexes(imagePath);
    }

    /// <summary>
    /// Reads the WinRE inside one index by mounting it read-only. Takes a minute or two; returns null when the
    /// index has no WinRE.
    /// </summary>
    public async Task<WinREInfo?> GetWinREAsync(string imagePath, int index, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var mountPath = Path.Combine(Path.GetTempPath(), "Familiar", "info-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await using var image = await Servicing.MountedImage.MountReadOnlyAsync(imagePath, index, mountPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var winRE = Path.Combine(mountPath, "Windows", "System32", "Recovery", "Winre.wim");
            if (!File.Exists(winRE))
            {
                return null;
            }

            var info = GetIndexes(winRE)[0];
            return new WinREInfo(info.Version, info.DefaultLanguage, info.Languages, new FileInfo(winRE).Length);
        }
        finally
        {
            try
            {
                Directory.Delete(mountPath);
            }
            catch (IOException)
            {
                // Left for 'familiar cleanup'.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DismApi.Shutdown();
    }
}
