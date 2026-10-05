using Microsoft.Dism;

namespace Familiar.Core.Images;

/// <summary>
/// Owns the DISM API lifetime for the process. Create one per host (CLI run, app session) and
/// dispose it on exit; DISM does not reliably support re-initializing after shutdown.
/// </summary>
public sealed class DismSession : IDisposable
{
    private bool _disposed;

    public DismSession()
    {
        if (!ProcessElevation.IsElevated)
        {
            throw new UnauthorizedAccessException("The DISM API requires administrator rights.");
        }

        DismApi.Initialize(DismLogLevel.LogErrors);
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
                i.DefaultLanguage?.Name,
                WindowsReleaseCatalog.Resolve(i.ProductVersion.Build, i.InstallationType)))
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
