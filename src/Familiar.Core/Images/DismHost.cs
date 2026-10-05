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

        return DismApi.GetImageInfo(imagePath)
            .Select(i => new ImageIndexInfo(
                i.ImageIndex,
                i.ImageName,
                i.EditionId,
                i.InstallationType,
                i.Architecture.ToString(),
                i.ProductVersion,
                i.DefaultLanguage?.Name))
            .ToList();
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
