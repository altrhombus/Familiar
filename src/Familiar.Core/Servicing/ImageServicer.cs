using Familiar.Core.Images;
using Microsoft.Dism;

namespace Familiar.Core.Servicing;

/// <summary>
/// What a build does to image files: read an index, export one, mount one. The real one is
/// <see cref="DismImageServicer"/>; tests use a fake so build logic runs without DISM or elevation.
/// </summary>
public interface IImageServicer
{
    /// <summary>One index of a WIM or ESD file, or null when it has no such index.</summary>
    ImageIndexInfo? GetIndex(string imagePath, int index);

    /// <summary>Copies one index into another WIM (dism.exe /Export-Image), optionally renaming it.</summary>
    Task<DismCommandResult> ExportAsync(
        string sourceImage, int sourceIndex, string destinationImage, string? destinationName = null,
        IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Mounts an index read-write into an empty folder.</summary>
    Task<IMountedImage> MountAsync(
        string imagePath, int index, string mountPath, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Image files through the DISM API and dism.exe. Needs an initialized <see cref="DismHost"/>.</summary>
public sealed class DismImageServicer(DismCommand? dism = null) : IImageServicer
{
    private readonly DismCommand _dism = dism ?? new DismCommand();

    public ImageIndexInfo? GetIndex(string imagePath, int index) =>
        ReadIndexes(imagePath).FirstOrDefault(i => i.Index == index);

    public Task<DismCommandResult> ExportAsync(
        string sourceImage, int sourceIndex, string destinationImage, string? destinationName = null,
        IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        _dism.ExportImageAsync(sourceImage, sourceIndex, destinationImage, destinationName, progress, cancellationToken);

    public async Task<IMountedImage> MountAsync(
        string imagePath, int index, string mountPath, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        await MountedImage.MountAsync(imagePath, index, mountPath, progress, _dism, cancellationToken).ConfigureAwait(false);

    /// <summary>Every index in a WIM or ESD file.</summary>
    internal static IReadOnlyList<ImageIndexInfo> ReadIndexes(string imagePath) =>
        DismApi.GetImageInfo(imagePath)
            .Select(i => new ImageIndexInfo(
                i.ImageIndex,
                i.ImageName,
                i.EditionId,
                i.InstallationType,
                i.Architecture.ToString(),
                i.ProductVersion,
                i.DefaultLanguage?.Name,
                i.Languages.Select(l => l.Name).ToList()))
            .ToList();
}
