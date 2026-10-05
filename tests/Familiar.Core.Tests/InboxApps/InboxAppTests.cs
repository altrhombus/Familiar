using System.IO.Compression;
using System.Text;
using Familiar.Core.InboxApps;

namespace Familiar.Core.Tests.InboxApps;

public sealed class InboxAppTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"), "packages");

    public InboxAppTests()
    {
        Directory.CreateDirectory(_folder);

        // A bundle like Microsoft.BingNews: full x64 app plus a stub listed with a backslash path.
        CreateBundle("Contoso.News_8wekyb3d8bbwe.msixbundle", "Contoso.News", "4.1.0.0",
            ("News_4.1.0.0_x64.msix", "x64", AppManifest("Contoso.News", "4.1.0.0", "x64", ("Contoso.UI.Xaml.2.8", "8.0.0.0"), ("Contoso.VCLibs", "14.0.0.0"))),
            (@"AppxMetadata\Stub\NewsStub_1.0.0.0_x64.msix", "x64", AppManifest("Contoso.News", "1.0.0.0", "x64")));
        License("Contoso.News_8wekyb3d8bbwe.xml");

        // A single-architecture app like MSTeams_8wekyb3d8bbwe.x64.msix.
        CreatePackage("Contoso.Chat_8wekyb3d8bbwe.x64.msix", AppManifest("Contoso.Chat", "2.0.0.0", "x64", ("Contoso.VCLibs", "14.0.0.0")));
        License("Contoso.Chat_8wekyb3d8bbwe.x64.xml");
        CreatePackage("Contoso.Chat_8wekyb3d8bbwe.arm64.msix", AppManifest("Contoso.Chat", "2.0.0.0", "arm64"));
        License("Contoso.Chat_8wekyb3d8bbwe.arm64.xml");

        CreatePackage("Contoso.UI.Xaml.x64.2.8.appx", AppManifest("Contoso.UI.Xaml.2.8", "8.2310.0.0", "x64", framework: true));
        CreatePackage("Contoso.UI.Xaml.arm64.2.8.appx", AppManifest("Contoso.UI.Xaml.2.8", "8.2310.0.0", "arm64", framework: true));
        CreatePackage("Contoso.VCLibs.x64.14.00.appx", AppManifest("Contoso.VCLibs", "14.0.30000.0", "x64", framework: true));
        CreatePackage("ContosoPWA.msix", AppManifest("Contoso.Pwa", "1.0.0.0", "neutral"));
    }

    public void Dispose()
    {
        Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
    }

    [Fact]
    public void Scan_SeparatesAppsFrameworksAndUnlicensedPackages()
    {
        var media = InboxAppMedia.Scan(Path.GetDirectoryName(_folder)!);

        Assert.Equal(_folder, media.PackagesPath);
        Assert.Equal(["Contoso.Chat", "Contoso.Chat", "Contoso.News"], media.Apps.Select(a => a.Identity.Name).Order());
        Assert.Equal(3, media.Frameworks.Count);
        Assert.Contains(media.Skipped, s => s.StartsWith("ContosoPWA.msix", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ReadsBundleIdentityAsNeutral()
    {
        var news = InboxAppMedia.Scan(_folder).Apps.Single(a => a.Identity.Name == "Contoso.News");

        Assert.Equal("neutral", news.Identity.Architecture);
        Assert.Equal(new Version(4, 1, 0, 0), news.Identity.Version);
    }

    [Fact]
    public void ReadDependencies_UsesFullAppNotStub()
    {
        var dependencies = AppPackageManifest.ReadDependencies(Path.Combine(_folder, "Contoso.News_8wekyb3d8bbwe.msixbundle"), "x64");

        Assert.Equal(["Contoso.UI.Xaml.2.8", "Contoso.VCLibs"], dependencies.Select(d => d.Name));
    }

    [Fact]
    public void Plan_ReprovisionsOnlyAppsInTheImage_WithMatchingFrameworks()
    {
        var media = InboxAppMedia.Scan(_folder);

        var plan = InboxAppPlanner.Plan(media, [new ProvisionedApp("Contoso.News", new Version(4, 0, 0, 0))], "x64");

        var action = Assert.IsType<AddProvisionedAppAction>(Assert.Single(plan.Actions));
        Assert.EndsWith("Contoso.News_8wekyb3d8bbwe.msixbundle", action.PackagePath, StringComparison.Ordinal);
        Assert.EndsWith("Contoso.News_8wekyb3d8bbwe.xml", action.LicensePath, StringComparison.Ordinal);
        Assert.Equal(["Contoso.UI.Xaml.x64.2.8.appx", "Contoso.VCLibs.x64.14.00.appx"], action.DependencyPaths.Select(Path.GetFileName));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Plan_PicksTheImageArchitecture()
    {
        var plan = InboxAppPlanner.Plan(InboxAppMedia.Scan(_folder), [new ProvisionedApp("Contoso.Chat", new Version(1, 0))], "arm64");

        Assert.EndsWith(".arm64.msix", ((AddProvisionedAppAction)Assert.Single(plan.Actions)).PackagePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_SkipsAppsNewerInTheImage()
    {
        var plan = InboxAppPlanner.Plan(InboxAppMedia.Scan(_folder), [new ProvisionedApp("Contoso.News", new Version(9, 0))], "x64");

        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Warnings, w => w.Contains("newer than the media", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_WarnsWhenAFrameworkIsMissingForTheArchitecture()
    {
        // arm64 Contoso.News needs Contoso.VCLibs, which only exists for x64 here. The bundle has no arm64 app,
        // so dependencies come from nothing; use a reader that reports the dependency to exercise the warning.
        var plan = InboxAppPlanner.Plan(
            InboxAppMedia.Scan(_folder),
            [new ProvisionedApp("Contoso.News", new Version(1, 0))],
            "arm64",
            (_, _) => [new AppPackageDependency("Contoso.VCLibs", new Version(14, 0))]);

        Assert.Single(plan.Actions);
        Assert.Contains(plan.Warnings, w => w.Contains("Contoso.VCLibs", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_LeavesRemovedAppsAlone()
    {
        Assert.Empty(InboxAppPlanner.Plan(InboxAppMedia.Scan(_folder), [], "x64").Actions);
    }

    private static string AppManifest(string name, string version, string architecture, params (string Name, string MinVersion)[] dependencies) =>
        AppManifest(name, version, architecture, framework: false, dependencies);

    private static string AppManifest(string name, string version, string architecture, bool framework, params (string Name, string MinVersion)[] dependencies)
    {
        var deps = string.Concat(dependencies.Select(d => $"""<PackageDependency Name="{d.Name}" MinVersion="{d.MinVersion}" Publisher="CN=Contoso" />"""));
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{name}" Version="{version}" ProcessorArchitecture="{architecture}" Publisher="CN=Contoso" />
              <Properties><Framework>{(framework ? "true" : "false")}</Framework></Properties>
              <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0" />{deps}</Dependencies>
            </Package>
            """;
    }

    private void CreatePackage(string fileName, string manifest)
    {
        using var zip = ZipFile.Open(Path.Combine(_folder, fileName), ZipArchiveMode.Create);
        Write(zip, "AppxManifest.xml", Encoding.UTF8.GetBytes(manifest));
    }

    private void CreateBundle(string fileName, string name, string version, params (string FileName, string Architecture, string Manifest)[] packages)
    {
        var entries = string.Concat(packages.Select(p => $"""<Package Type="application" Architecture="{p.Architecture}" FileName="{p.FileName}" />"""));
        var bundleManifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
              <Identity Name="{name}" Version="{version}" Publisher="CN=Contoso" />
              <Packages>{entries}</Packages>
            </Bundle>
            """;

        using var zip = ZipFile.Open(Path.Combine(_folder, fileName), ZipArchiveMode.Create);
        Write(zip, "AppxMetadata/AppxBundleManifest.xml", Encoding.UTF8.GetBytes(bundleManifest));
        foreach (var package in packages)
        {
            using var inner = new MemoryStream();
            using (var innerZip = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(innerZip, "AppxManifest.xml", Encoding.UTF8.GetBytes(package.Manifest));
            }

            Write(zip, package.FileName.Replace('\\', '/'), inner.ToArray());
        }
    }

    private void License(string fileName) => File.WriteAllText(Path.Combine(_folder, fileName), "<License />");

    private static void Write(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        stream.Write(content);
    }
}
