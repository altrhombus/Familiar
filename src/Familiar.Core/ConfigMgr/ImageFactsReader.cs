using System.Globalization;
using Familiar.Core.Images;
using Familiar.Core.Releases;

namespace Familiar.Core.ConfigMgr;

public static class ImageFactsReader
{
    /// <summary>
    /// Reads the facts templates and the downgrade check use from a WIM's first index. Requires an initialized
    /// DISM API. Release and update details come from the release data and are null when it does not know the build.
    /// </summary>
    public static ImageFacts Read(string wimPath, ReleaseCatalog? catalog) =>
        FromIndex(
            Servicing.DismImageServicer.ReadIndexes(wimPath) is [var first, ..] ? first : throw new ConfigMgrException($"{wimPath} contains no images."),
            catalog);

    /// <summary>The facts for one image index, as <see cref="Read"/> describes.</summary>
    public static ImageFacts FromIndex(ImageIndexInfo info, ReleaseCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(info);
        var version = info.Version;
        var release = catalog?.Resolve(version.Build, info.InstallationType);
        var update = release is null ? null : catalog!.FindInstalledUpdate(release, version.Revision);

        return new ImageFacts(
            release?.Version,
            info.Architecture.ToUpperInvariant(),
            info.EditionId,
            string.Create(CultureInfo.InvariantCulture, $"{version.Build}.{version.Revision}"),
            update?.KbArticle,
            update?.ReleaseDate);
    }
}
