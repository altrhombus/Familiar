using Familiar.Core.InboxApps;
using Familiar.Core.Languages;

namespace Familiar.Core.Servicing;

/// <summary>
/// A WIM index mounted for servicing, as a build sees it. Disposing without committing discards every change.
/// The real one is <see cref="MountedImage"/>; tests use a fake so build logic runs without DISM or elevation.
/// </summary>
public interface IMountedImage : IAsyncDisposable
{
    string MountPath { get; }

    /// <inheritdoc cref="MountedImage.GetServicingBaselineBuild"/>
    int? GetServicingBaselineBuild();

    /// <inheritdoc cref="MountedImage.ReadWindowsVersion"/>
    OfflineWindowsVersion ReadWindowsVersion();

    ImageLanguageState GetLanguageState();

    IReadOnlyList<PackageIdentity> GetPackages();

    IReadOnlyList<ProvisionedApp> GetProvisionedApps();

    /// <summary>Applies one planned action through the DISM API.</summary>
    Task ApplyAsync(ServicingAction action, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Adds an update with dism.exe (needed for .msu since 24H2); <paramref name="scratchPath"/> keeps it out of the image.</summary>
    Task<DismCommandResult> AddUpdateAsync(string packagePath, string scratchPath, string description, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Sets UI language, locale, input and time zone defaults to one language (dism.exe /Set-AllIntl).</summary>
    Task<DismCommandResult> SetAllInternationalAsync(string language, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Removes superseded components (dism.exe /StartComponentCleanup, with /ResetBase when asked).</summary>
    Task<DismCommandResult> CleanupComponentsAsync(bool resetBase, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Saves changes to the WIM and unmounts.</summary>
    Task CommitAsync(IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Unmounts without saving.</summary>
    Task DiscardAsync(IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default);
}
