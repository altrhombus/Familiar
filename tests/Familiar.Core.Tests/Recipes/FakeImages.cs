using Familiar.Core.Images;
using Familiar.Core.InboxApps;
using Familiar.Core.Languages;
using Familiar.Core.Servicing;

namespace Familiar.Core.Tests.Recipes;

/// <summary>
/// Stands in for DISM in build tests. Mount folders are real (temporary) folders, so the build's file handling runs
/// as it does on a real machine, and every DISM operation is recorded, in order, in <see cref="Calls"/>.
/// </summary>
internal sealed class FakeImages(string sourcePath, string outputPath) : IImageServicer, IMountTable
{
    private readonly List<FakeMount> _mounted = [];

    public List<string> Calls { get; } = [];

    public ImageIndexInfo Source { get; init; } = new(3, "Windows 11 Enterprise", "Enterprise", "Client", "AMD64", new Version(10, 0, 26200, 6584), "en-US", ["en-US"]);

    public Version WinREBefore { get; init; } = new(10, 0, 26100, 5059);

    public Version WinREAfter { get; set; } = new(10, 0, 26100, 9545);

    /// <summary>Put a Winre.wim (read-only and hidden, as in real images) into the main image's mount.</summary>
    public bool WithWinRE { get; init; } = true;

    public byte[] WinREContent { get; init; } = [1, 1, 1, 1];

    /// <summary>What exporting WinRE produces.</summary>
    public byte[] ServicedWinREContent { get; init; } = [2, 2];

    public int? BaselineBuild { get; init; } = 26100;

    public bool RegistryUnreadable { get; init; }

    /// <summary>dism.exe exit codes by package file name, or "winre:" plus the name for WinRE only; 0 for anything else.</summary>
    public Dictionary<string, int> AddUpdateExitCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs before every applied action, with its call text; tests use it to cancel or fail mid-build.</summary>
    public Action<string>? OnApply { get; init; }

    /// <summary>Exports to a file with this name fail with exit code 1 after writing part of it.</summary>
    public string? FailExportTo { get; init; }

    /// <summary>Unmounting without saving fails and leaves the image mounted, as when a file in the mount is in use.</summary>
    public bool FailDiscard { get; init; }

    /// <summary>What Winre.wim held, and its attributes, when the main image was committed.</summary>
    public (byte[] Content, FileAttributes Attributes)? CommittedWinRE { get; private set; }

    public ImageIndexInfo? GetIndex(string imagePath, int index) => Path.GetFileName(imagePath) switch
    {
        _ when SamePath(imagePath, sourcePath) => Source,
        "winre.wim" => Source with { Index = 1, Name = "Microsoft Windows Recovery Environment", Version = WinREBefore },
        "winre-final.wim" => Source with { Index = 1, Name = "Microsoft Windows Recovery Environment", Version = WinREAfter },
        _ => Source with { Index = 1 },
    };

    public Task<DismCommandResult> ExportAsync(
        string sourceImage, int sourceIndex, string destinationImage, string? destinationName = null,
        IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"export {Name(sourceImage)} -> {Name(destinationImage)}" + (destinationName is null ? string.Empty : $" as {destinationName}"));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationImage)!);
        File.WriteAllBytes(destinationImage, Path.GetFileName(destinationImage) == "winre-final.wim" ? ServicedWinREContent : [1]);
        var failed = string.Equals(Path.GetFileName(destinationImage), FailExportTo, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new DismCommandResult(failed ? 1 : 0, failed ? "Error: 112 There is not enough space on the disk." : string.Empty));
    }

    public Task<IMountedImage> MountAsync(
        string imagePath, int index, string mountPath, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var label = Path.GetFileName(imagePath) == "winre.wim" ? "winre" : "image";
        Calls.Add($"{label} mount");
        Directory.CreateDirectory(mountPath);
        if (label == "image" && WithWinRE)
        {
            var winRE = Path.Combine(mountPath, "Windows", "System32", "Recovery", "Winre.wim");
            Directory.CreateDirectory(Path.GetDirectoryName(winRE)!);
            File.WriteAllBytes(winRE, WinREContent);
            File.SetAttributes(winRE, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        }

        var mount = new FakeMount(this, label, imagePath, mountPath);
        _mounted.Add(mount);
        return Task.FromResult<IMountedImage>(mount);
    }

    public IReadOnlyList<MountedImageInfo> GetMountedImages() =>
        _mounted.Select(m => new MountedImageInfo(m.MountPath, m.ImagePath, "Ok")).ToList();

    public void Discard(string mountPath) => throw new NotSupportedException("Builds do not discard through the mount table.");

    public void CleanupMountpoints()
    {
    }

    private string Name(string path) =>
        SamePath(path, sourcePath) ? "source" : SamePath(path, outputPath) ? "output" : Path.GetFileName(path);

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class FakeMount(FakeImages images, string label, string imagePath, string mountPath) : IMountedImage
    {
        private bool _unmounted;

        public string ImagePath => imagePath;

        public string MountPath => mountPath;

        public int? GetServicingBaselineBuild() => images.BaselineBuild;

        public OfflineWindowsVersion ReadWindowsVersion() => images.RegistryUnreadable
            ? throw new InvalidOperationException("The SOFTWARE hive could not be read.")
            : new OfflineWindowsVersion("25H2", "2009", images.Source.Version.Build, images.Source.Version.Revision, images.Source.EditionId);

        public ImageLanguageState GetLanguageState() =>
            new(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(["en-US"], StringComparer.OrdinalIgnoreCase));

        public IReadOnlyList<PackageIdentity> GetPackages() => [];

        public IReadOnlyList<ProvisionedApp> GetProvisionedApps() => [];

        public Task ApplyAsync(ServicingAction action, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var call = action switch
            {
                AddPackageAction package => $"{label} apply {Path.GetFileName(package.PackagePath)}",
                AddCapabilityAction capability => $"{label} capability {capability.CapabilityName}",
                _ => $"{label} {action.Description}",
            };
            images.OnApply?.Invoke(call);
            images.Calls.Add(call);
            return Task.CompletedTask;
        }

        public Task<DismCommandResult> AddUpdateAsync(string packagePath, string scratchPath, string description, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var name = Path.GetFileName(packagePath);
            images.Calls.Add($"{label} dism-add {name}");
            var exitCode = images.AddUpdateExitCodes.TryGetValue($"{label}:{name}", out var specific) ? specific : images.AddUpdateExitCodes.GetValueOrDefault(name);
            return Task.FromResult(new DismCommandResult(exitCode, exitCode == 0 ? string.Empty : $"Error: {exitCode}"));
        }

        public Task<DismCommandResult> SetAllInternationalAsync(string language, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            images.Calls.Add($"{label} set-all-intl {language}");
            return Task.FromResult(new DismCommandResult(0, string.Empty));
        }

        public Task<DismCommandResult> CleanupComponentsAsync(bool resetBase, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            images.Calls.Add($"{label} cleanup" + (resetBase ? " /ResetBase" : string.Empty));
            return Task.FromResult(new DismCommandResult(0, string.Empty));
        }

        public Task CommitAsync(IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            images.Calls.Add($"{label} commit");
            var winRE = Path.Combine(mountPath, "Windows", "System32", "Recovery", "Winre.wim");
            if (label == "image" && File.Exists(winRE))
            {
                images.CommittedWinRE = (File.ReadAllBytes(winRE), File.GetAttributes(winRE));
            }

            Unmount();
            return Task.CompletedTask;
        }

        public Task DiscardAsync(IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            images.Calls.Add($"{label} discard");
            if (images.FailDiscard)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            Unmount();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_unmounted)
            {
                await DiscardAsync().ConfigureAwait(false);
            }
        }

        // DISM empties the mount folder when it unmounts.
        private void Unmount()
        {
            foreach (var file in Directory.EnumerateFiles(mountPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(mountPath, recursive: true);
            Directory.CreateDirectory(mountPath);
            _unmounted = true;
            images._mounted.Remove(this);
        }
    }
}
