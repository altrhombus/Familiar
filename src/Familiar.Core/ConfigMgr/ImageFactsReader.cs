using System.Globalization;
using Familiar.Core.Releases;
using Microsoft.Dism;

namespace Familiar.Core.ConfigMgr;

public static class ImageFactsReader
{
    /// <summary>
    /// Reads the facts templates and the downgrade check use from a WIM's first index. Requires an initialized
    /// DISM API. Release and update details come from the release data and are null when it does not know the build.
    /// </summary>
    public static ImageFacts Read(string wimPath, ReleaseCatalog? catalog)
    {
        var info = DismApi.GetImageInfo(wimPath).FirstOrDefault()
            ?? throw new ConfigMgrException($"{wimPath} contains no images.");

        var version = info.ProductVersion;
        var release = catalog?.Resolve(version.Build, info.InstallationType);
        var update = release is null ? null : catalog!.FindInstalledUpdate(release, version.Revision);

        return new ImageFacts(
            release?.Version,
            info.Architecture.ToString().ToUpperInvariant(),
            info.EditionId,
            string.Create(CultureInfo.InvariantCulture, $"{version.Build}.{version.Revision}"),
            update?.KbArticle,
            update?.ReleaseDate);
    }
}
