namespace Familiar.Core.InboxApps;

/// <summary>An app on the Inbox Apps media, with the license file DISM needs to provision it.</summary>
public sealed record InboxApp(string Path, string LicensePath, AppPackageIdentity Identity);

/// <summary>A framework package (VCLibs, UI.Xaml, Windows App Runtime, ...) that apps depend on.</summary>
public sealed record FrameworkPackage(string Path, AppPackageIdentity Identity);

/// <summary>
/// Inventory of the Windows Inbox Apps ISO. Apps are packages with a matching license (.xml); frameworks are
/// packages whose manifest marks them as frameworks. Identities come from the manifests, not file names.
/// </summary>
public sealed class InboxAppMedia
{
    private static readonly string[] PackageExtensions = [".appx", ".msix", ".appxbundle", ".msixbundle"];

    private InboxAppMedia(string packagesPath, IReadOnlyList<InboxApp> apps, IReadOnlyList<FrameworkPackage> frameworks, IReadOnlyList<string> skipped)
    {
        PackagesPath = packagesPath;
        Apps = apps;
        Frameworks = frameworks;
        Skipped = skipped;
    }

    public string PackagesPath { get; }

    public IReadOnlyList<InboxApp> Apps { get; }

    public IReadOnlyList<FrameworkPackage> Frameworks { get; }

    /// <summary>Packages that are neither licensed apps nor frameworks (for example PWA stubs), with the reason.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>Scans an Inbox Apps ISO root or its <c>packages</c> folder.</summary>
    public static InboxAppMedia Scan(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var nested = System.IO.Path.Combine(path, "packages");
        var folder = Directory.Exists(nested) ? nested : path;
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Inbox apps media not found: {path}");
        }

        var apps = new List<InboxApp>();
        var frameworks = new List<FrameworkPackage>();
        var skipped = new List<string>();

        foreach (var file in Directory.EnumerateFiles(folder).Where(f => PackageExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            AppPackageIdentity identity;
            try
            {
                identity = AppPackageManifest.ReadIdentity(file);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
            {
                skipped.Add($"{System.IO.Path.GetFileName(file)}: unreadable manifest ({ex.Message})");
                continue;
            }

            var license = System.IO.Path.ChangeExtension(file, ".xml");
            if (File.Exists(license))
            {
                apps.Add(new InboxApp(file, license, identity));
            }
            else if (identity.IsFramework)
            {
                frameworks.Add(new FrameworkPackage(file, identity));
            }
            else
            {
                skipped.Add($"{System.IO.Path.GetFileName(file)}: no license file");
            }
        }

        return new InboxAppMedia(folder, apps, frameworks, skipped);
    }

    internal static InboxAppMedia Create(string packagesPath, IReadOnlyList<InboxApp> apps, IReadOnlyList<FrameworkPackage> frameworks) =>
        new(packagesPath, apps, frameworks, []);
}
