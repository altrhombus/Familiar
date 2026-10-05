using Familiar.Core.Servicing;

namespace Familiar.Core.InboxApps;

/// <summary>An app already provisioned in the image, from DISM GetProvisionedAppxPackages.</summary>
public sealed record ProvisionedApp(string DisplayName, Version Version);

/// <summary>Provisions an app with its dependencies and license for every region.</summary>
public sealed record AddProvisionedAppAction(
    string PackagePath, IReadOnlyList<string> DependencyPaths, string LicensePath, string Description) : ServicingAction(Description);

public static class InboxAppPlanner
{
    /// <summary>
    /// Re-provisions the apps already in the image from the Inbox Apps media, so their bundles add resource
    /// packages for languages added since they were first provisioned. Apps the image does not have (because
    /// they were never there or were removed) are left alone.
    /// </summary>
    /// <param name="dependencyReader">Reads an app's dependencies; replaceable for tests.</param>
    public static ServicingPlan Plan(
        InboxAppMedia media,
        IEnumerable<ProvisionedApp> provisioned,
        string architecture,
        Func<InboxApp, string, IReadOnlyList<AppPackageDependency>>? dependencyReader = null)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(provisioned);
        dependencyReader ??= (app, arch) => AppPackageManifest.ReadDependencies(app.Path, arch);

        var actions = new List<ServicingAction>();
        var warnings = new List<string>();

        foreach (var installed in provisioned.DistinctBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var app = SelectApp(media, installed.DisplayName, architecture);
            if (app is null)
            {
                continue;
            }

            if (app.Identity.Version < installed.Version)
            {
                warnings.Add($"Skipping {installed.DisplayName}: the image has {installed.Version}, newer than the media's {app.Identity.Version}.");
                continue;
            }

            IReadOnlyList<AppPackageDependency> dependencies;
            try
            {
                dependencies = dependencyReader(app, architecture);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
            {
                warnings.Add($"Skipping {installed.DisplayName}: could not read its dependencies ({ex.Message}).");
                continue;
            }

            var dependencyPaths = new List<string>();
            foreach (var dependency in dependencies)
            {
                var framework = SelectFramework(media, dependency, architecture);
                if (framework is null)
                {
                    // Often already in the image at a sufficient version; DISM reports it if not.
                    warnings.Add($"{installed.DisplayName} needs {dependency.Name} {dependency.MinVersion}, which is not on the media for {architecture}.");
                    continue;
                }

                dependencyPaths.Add(framework.Path);
            }

            actions.Add(new AddProvisionedAppAction(app.Path, dependencyPaths, app.LicensePath, $"Re-provision {installed.DisplayName} {app.Identity.Version}"));
        }

        return new ServicingPlan(actions, warnings, []);
    }

    // Bundles cover every architecture; single-architecture packages must match the image.
    private static InboxApp? SelectApp(InboxAppMedia media, string name, string architecture) =>
        media.Apps
            .Where(a => string.Equals(a.Identity.Name, name, StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Identity.Architecture is "neutral" || string.Equals(a.Identity.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => string.Equals(a.Identity.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.Identity.Version)
            .FirstOrDefault();

    private static FrameworkPackage? SelectFramework(InboxAppMedia media, AppPackageDependency dependency, string architecture) =>
        media.Frameworks
            .Where(f => string.Equals(f.Identity.Name, dependency.Name, StringComparison.OrdinalIgnoreCase)
                && f.Identity.Version >= dependency.MinVersion
                && (f.Identity.Architecture is "neutral" || string.Equals(f.Identity.Architecture, architecture, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.Identity.Version)
            .FirstOrDefault();
}
