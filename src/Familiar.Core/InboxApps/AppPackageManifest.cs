using System.IO.Compression;
using System.Xml.Linq;

namespace Familiar.Core.InboxApps;

/// <summary>Identity from an AppxManifest.xml or AppxBundleManifest.xml.</summary>
/// <param name="Architecture">"x64", "arm64", "x86", "arm" or "neutral".</param>
public sealed record AppPackageIdentity(string Name, Version Version, string Architecture, bool IsFramework);

public sealed record AppPackageDependency(string Name, Version MinVersion);

/// <summary>Reads app package manifests without extracting packages.</summary>
internal static class AppPackageManifest
{
    private const string PackageManifest = "AppxManifest.xml";
    private const string BundleManifest = "AppxMetadata/AppxBundleManifest.xml";

    public static bool IsBundle(string path) =>
        path.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase);

    public static AppPackageIdentity ReadIdentity(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return IsBundle(path) ? ParseBundleIdentity(Read(zip, BundleManifest)) : ParsePackage(Read(zip, PackageManifest)).Identity;
    }

    /// <summary>
    /// Dependencies of the application package for <paramref name="architecture"/>. For bundles that means reading
    /// the matching inner package, which is stored uncompressed but not seekable, so it is copied to memory.
    /// </summary>
    public static IReadOnlyList<AppPackageDependency> ReadDependencies(string path, string architecture)
    {
        using var zip = ZipFile.OpenRead(path);
        if (!IsBundle(path))
        {
            return ParsePackage(Read(zip, PackageManifest)).Dependencies;
        }

        var inner = SelectApplicationPackage(Read(zip, BundleManifest), architecture);
        if (inner is null)
        {
            return [];
        }

        var entry = zip.GetEntry(inner) ?? throw new InvalidDataException($"{Path.GetFileName(path)} lists {inner} but does not contain it.");
        using var buffer = new MemoryStream(checked((int)entry.Length));
        using (var stream = entry.Open())
        {
            stream.CopyTo(buffer);
        }

        buffer.Position = 0;
        using var innerZip = new ZipArchive(buffer, ZipArchiveMode.Read);
        return ParsePackage(Read(innerZip, PackageManifest)).Dependencies;
    }

    internal static (AppPackageIdentity Identity, IReadOnlyList<AppPackageDependency> Dependencies) ParsePackage(XDocument manifest)
    {
        var root = manifest.Root ?? throw new InvalidDataException("Empty AppxManifest.");
        var identity = Child(root, "Identity") ?? throw new InvalidDataException("AppxManifest has no Identity.");
        var framework = Child(Child(root, "Properties"), "Framework")?.Value;

        var dependencies = Child(root, "Dependencies")?.Elements()
            .Where(e => e.Name.LocalName == "PackageDependency")
            .Select(e => new AppPackageDependency(
                (string?)e.Attribute("Name") ?? string.Empty,
                Version.TryParse((string?)e.Attribute("MinVersion"), out var min) ? min : new Version(0, 0)))
            .Where(d => d.Name.Length > 0)
            .ToList() ?? [];

        return (ToIdentity(identity, isFramework: string.Equals(framework, "true", StringComparison.OrdinalIgnoreCase)), dependencies);
    }

    internal static AppPackageIdentity ParseBundleIdentity(XDocument manifest)
    {
        var identity = Child(manifest.Root, "Identity") ?? throw new InvalidDataException("AppxBundleManifest has no Identity.");
        return ToIdentity(identity, isFramework: false) with { Architecture = "neutral" };
    }

    /// <summary>The application (not resource) package for an architecture; x86 serves x64 images when nothing better exists.</summary>
    internal static string? SelectApplicationPackage(XDocument bundleManifest, string architecture)
    {
        // Bundle manifests use backslashes ("AppxMetadata\Stub\x.msix"); zip entry names use forward slashes.
        var applications = Child(bundleManifest.Root, "Packages")?.Elements()
            .Where(e => e.Name.LocalName == "Package" && string.Equals((string?)e.Attribute("Type"), "application", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Architecture: (string?)e.Attribute("Architecture") ?? "neutral", FileName: ((string?)e.Attribute("FileName"))?.Replace('\\', '/')))
            .Where(p => p.FileName is not null)
            .ToList() ?? [];

        // Some bundles also carry a small stub that downloads the app later; its dependencies are not the app's.
        string[] preference = architecture == "x64" ? ["x64", "neutral", "x86"] : [architecture, "neutral"];
        return preference
            .Select(arch => applications
                .Where(p => string.Equals(p.Architecture, arch, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.FileName!.StartsWith("AppxMetadata/Stub/", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault().FileName)
            .FirstOrDefault(f => f is not null);
    }

    private static AppPackageIdentity ToIdentity(XElement identity, bool isFramework) => new(
        (string?)identity.Attribute("Name") ?? string.Empty,
        Version.TryParse((string?)identity.Attribute("Version"), out var version) ? version : new Version(0, 0),
        ((string?)identity.Attribute("ProcessorArchitecture") ?? "neutral").ToLowerInvariant(),
        isFramework);

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static XDocument Read(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException($"Package has no {entryName}.");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
