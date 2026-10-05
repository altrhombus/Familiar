namespace Familiar.Core.Images;

/// <summary>One index inside a WIM or ESD file.</summary>
/// <param name="Release">
/// Best-effort release from image metadata. Confirm against the mounted image's registry before
/// relying on it for language or update selection; see <see cref="WindowsReleaseCatalog.Resolve"/>.
/// </param>
public sealed record ImageIndexInfo(
    int Index,
    string Name,
    string EditionId,
    string InstallationType,
    string Architecture,
    Version Version,
    string? DefaultLanguage,
    WindowsRelease? Release);
