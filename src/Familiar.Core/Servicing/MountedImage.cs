using Familiar.Core.Languages;
using Microsoft.Dism;

namespace Familiar.Core.Servicing;

/// <summary>
/// A WIM index mounted for servicing. Dispose without committing discards all changes, so an exception
/// partway through a build never leaves a half-serviced image behind.
/// </summary>
public sealed class MountedImage : IAsyncDisposable
{
    private DismSession? _session;
    private bool _unmounted;

    private MountedImage(string imagePath, int index, string mountPath, DismSession session)
    {
        ImagePath = imagePath;
        Index = index;
        MountPath = mountPath;
        _session = session;
    }

    public string ImagePath { get; }

    public int Index { get; }

    public string MountPath { get; }

    private DismSession Session => _session ?? throw new ObjectDisposedException(nameof(MountedImage));

    /// <summary>Mounts an index read-write. The DISM API must be initialized (see <see cref="Images.DismHost"/>).</summary>
    public static Task<MountedImage> MountAsync(
        string imagePath, int index, string mountPath, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        MountAsync(imagePath, index, mountPath, readOnly: false, progress, cancellationToken);

    /// <summary>Mounts an index for reading only; nothing can be saved.</summary>
    public static Task<MountedImage> MountReadOnlyAsync(
        string imagePath, int index, string mountPath, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        MountAsync(imagePath, index, mountPath, readOnly: true, progress, cancellationToken);

    private static async Task<MountedImage> MountAsync(
        string imagePath, int index, string mountPath, bool readOnly, IProgress<ServicingProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(mountPath);
        if (Directory.EnumerateFileSystemEntries(mountPath).Any())
        {
            throw new InvalidOperationException($"Mount folder {mountPath} is not empty. Run 'familiar cleanup' if a previous build was interrupted.");
        }

        var operation = $"Mount index {index}";
        progress?.Report(ServicingProgress.Started(operation));
        await DismApi.MountImageAsync(imagePath, mountPath, index, readOnly, DismMountImageOptions.None,
            Report(progress, operation), cancellationToken).ConfigureAwait(false);

        try
        {
            return new MountedImage(imagePath, index, mountPath, DismApi.OpenOfflineSession(mountPath));
        }
        catch
        {
            await DismApi.UnmountImageAsync(mountPath, commitChanges: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public IReadOnlyList<PackageIdentity> GetPackages() =>
        DismApi.GetPackages(Session)
            .Where(p => p.PackageState is DismPackageFeatureState.Installed or DismPackageFeatureState.InstallPending)
            .Select(p => PackageIdentity.TryParse(p.PackageName, out var identity) ? identity : null)
            .OfType<PackageIdentity>()
            .ToList();

    public IReadOnlyList<InboxApps.ProvisionedApp> GetProvisionedApps() =>
        DismApi.GetProvisionedAppxPackages(Session)
            .Select(p => new InboxApps.ProvisionedApp(p.DisplayName, p.Version))
            .ToList();

    /// <summary>Capability name to whether it is installed.</summary>
    public IReadOnlyDictionary<string, bool> GetCapabilities() =>
        DismApi.GetCapabilities(Session).ToDictionary(
            c => c.Name,
            c => c.State is DismPackageFeatureState.Installed or DismPackageFeatureState.InstallPending,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The build of the image's Foundation package. Enablement releases keep their base build here (25H2 reports
    /// 26100), and language and Features on Demand media must match it.
    /// </summary>
    public int? GetServicingBaselineBuild() =>
        GetPackages().FirstOrDefault(p => p.Name.Equals(PackageIdentity.FoundationPackage, StringComparison.OrdinalIgnoreCase))?.Version?.Build;

    /// <summary>DisplayVersion, build and edition from the image's own registry, via the hive DISM already loaded.</summary>
    public OfflineWindowsVersion ReadWindowsVersion()
    {
        var mountPoint = DismApi.GetRegistryMountPoint(Session, DismRegistryHive.Software);
        try
        {
            return OfflineRegistry.ReadVersion(mountPoint);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{ex.Message} (registry mount point: '{mountPoint}')", ex);
        }
    }

    public ImageLanguageState GetLanguageState()
    {
        var languages = GetPackages()
            .Where(p => p.Name.Equals(PackageIdentity.ClientLanguagePack, StringComparison.OrdinalIgnoreCase) && p.Language.Length > 0)
            .Select(p => LanguageTag.Normalize(p.Language));

        return new ImageLanguageState(GetCapabilities(), new HashSet<string>(languages, StringComparer.OrdinalIgnoreCase));
    }

    public async Task ApplyAsync(ServicingAction action, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        progress?.Report(ServicingProgress.Started(action.Description));
        var report = Report(progress, action.Description);

        switch (action)
        {
            case AddPackageAction package:
                await DismApi.AddPackageAsync(Session, package.PackagePath, ignoreCheck: false, preventPending: false,
                    report, cancellationToken).ConfigureAwait(false);
                break;

            case AddCapabilityAction capability:
                await DismApi.AddCapabilityAsync(Session, capability.CapabilityName, limitAccess: true,
                    [capability.SourcePath], report, cancellationToken).ConfigureAwait(false);
                break;

            case InboxApps.AddProvisionedAppAction app:
                // The DISM API has no async or progress overload for this; keep it off the caller's thread.
                await Task.Run(() => DismApi.AddProvisionedAppxPackage(Session, app.PackagePath, [.. app.DependencyPaths], [],
                    [app.LicensePath], customDataPath: null!, regions: "all", DismStubPackageOption.None), cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException($"Unsupported servicing action {action.GetType().Name}.");
        }
    }

    /// <summary>
    /// Runs a dism.exe operation against the mount. DISM allows one servicing session per image, so the API
    /// session is closed for the duration (otherwise dism.exe fails with error 183) and reopened afterwards.
    /// </summary>
    public async Task<T> RunExternalAsync<T>(Func<string, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_session is null || _unmounted, this);

        _session!.Dispose();
        _session = null;
        try
        {
            return await operation(MountPath).ConfigureAwait(false);
        }
        finally
        {
            _session = DismApi.OpenOfflineSession(MountPath);
        }
    }

    /// <summary>Saves changes to the WIM and unmounts.</summary>
    public Task CommitAsync(IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        UnmountAsync(commit: true, progress, cancellationToken);

    /// <summary>Unmounts without saving.</summary>
    public Task DiscardAsync(IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        UnmountAsync(commit: false, progress, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (!_unmounted)
        {
            await DiscardAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task UnmountAsync(bool commit, IProgress<ServicingProgress>? progress, CancellationToken cancellationToken)
    {
        if (_unmounted)
        {
            return;
        }

        // The session holds files open; it must close before DISM can unmount.
        _session?.Dispose();
        _session = null;

        var operation = commit ? "Save and unmount" : "Discard and unmount";
        progress?.Report(ServicingProgress.Started(operation));
        await DismApi.UnmountImageAsync(MountPath, commit, Report(progress, operation), cancellationToken).ConfigureAwait(false);
        _unmounted = true;
    }

    private static Progress<DismProgress>? Report(IProgress<ServicingProgress>? progress, string operation) =>
        progress is null
            ? null
            : new Progress<DismProgress>(p => progress.Report(new ServicingProgress(operation, p.Total > 0 ? 100.0 * p.Current / p.Total : null)));
}
