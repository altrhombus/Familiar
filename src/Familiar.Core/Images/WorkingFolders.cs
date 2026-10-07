using System.Globalization;
using Microsoft.Dism;

namespace Familiar.Core.Images;

/// <summary>An image mounted somewhere on this machine.</summary>
public sealed record MountedImageInfo(string MountPath, string ImagePath, string Status);

/// <summary>The machine's mounted images. A seam over the DISM API so cleanup can be tested without elevation.</summary>
public interface IMountTable
{
    IReadOnlyList<MountedImageInfo> GetMountedImages();

    /// <summary>Unmounts without saving.</summary>
    void Discard(string mountPath);

    /// <summary>Removes DISM's records of mounts whose folders are gone or damaged.</summary>
    void CleanupMountpoints();
}

/// <summary>Mounted images through the DISM API; needs an initialized <see cref="DismHost"/>.</summary>
public sealed class DismMountTable : IMountTable
{
    public IReadOnlyList<MountedImageInfo> GetMountedImages() =>
        DismApi.GetMountedImages().Select(m => new MountedImageInfo(m.MountPath, m.ImageFilePath, m.MountStatus.ToString())).ToList();

    public void Discard(string mountPath) => DismApi.UnmountImage(mountPath, commitChanges: false);

    public void CleanupMountpoints() => DismApi.CleanupMountpoints();
}

/// <param name="Discarded">Mount paths that were unmounted without saving.</param>
/// <param name="Removed">Working folders that were deleted.</param>
/// <param name="Problems">What could not be cleaned up, and why.</param>
public sealed record CleanupResult(IReadOnlyList<string> Discarded, IReadOnlyList<string> Removed, IReadOnlyList<string> Problems);

/// <summary>
/// The per-build folders under a recipe's working directory. Each one carries a marker file, and cleanup only ever
/// touches folders that have it, so pointing it at the wrong directory deletes nothing.
/// </summary>
public static class WorkingFolders
{
    public const string MarkerFileName = "familiar-working-folder.txt";

    /// <summary>Creates a new, marked folder for one build under <paramref name="workingDirectory"/>.</summary>
    public static string Create(string workingDirectory, DateTime now)
    {
        var folder = Path.Combine(workingDirectory, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, MarkerFileName),
            "Created by Familiar for one build. 'familiar cleanup' discards images mounted here and deletes this folder." + Environment.NewLine);
        return folder;
    }

    public static bool IsWorkingFolder(string folder) => File.Exists(Path.Combine(folder, MarkerFileName));

    /// <summary>Mounts inside <paramref name="folder"/>.</summary>
    public static IReadOnlyList<MountedImageInfo> MountsInside(string folder, IMountTable mounts)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return mounts.GetMountedImages()
            .Where(m => (Path.TrimEndingDirectorySeparator(Path.GetFullPath(m.MountPath)) + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Discards images mounted inside Familiar's working folders under <paramref name="workingDirectory"/>, then
    /// deletes those folders. Folders without the marker are left alone, and so is any folder that still has an image
    /// mounted in it: deleting into a live mount damages it.
    /// </summary>
    public static CleanupResult Clean(string workingDirectory, IMountTable mounts, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        log ??= _ => { };
        var discarded = new List<string>();
        var removed = new List<string>();
        var problems = new List<string>();

        if (!Directory.Exists(workingDirectory))
        {
            return new CleanupResult(discarded, removed, problems);
        }

        var folders = Directory.EnumerateDirectories(workingDirectory).ToList();
        foreach (var other in folders.Where(f => !IsWorkingFolder(f)))
        {
            log($"Leaving {other}: Familiar did not create it");
        }

        var ours = folders.Where(IsWorkingFolder).ToList();

        // Deepest first, in case one mount ever sits inside another.
        foreach (var mount in ours.SelectMany(f => MountsInside(f, mounts)).OrderByDescending(m => m.MountPath.Length))
        {
            log($"Discarding {mount.MountPath} ({mount.ImagePath}, {mount.Status})");
            try
            {
                mounts.Discard(mount.MountPath);
                discarded.Add(mount.MountPath);
            }
            catch (Exception ex) when (ex is DismException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"Could not discard {mount.MountPath}: {DismErrors.Describe(ex) ?? ex.Message}");
            }
        }

        try
        {
            mounts.CleanupMountpoints();
        }
        catch (Exception ex) when (ex is DismException or System.Runtime.InteropServices.COMException)
        {
            problems.Add($"Could not clean up DISM's mount records: {DismErrors.Describe(ex) ?? ex.Message}");
        }

        foreach (var folder in ours)
        {
            if (MountsInside(folder, mounts) is { Count: > 0 } left)
            {
                problems.Add($"Left {folder} in place: {left[0].MountPath} is still mounted. Close anything using it, then run cleanup again.");
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
                removed.Add(folder);
                log($"Removed {folder}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Could not remove {folder}: {ex.Message}");
            }
        }

        return new CleanupResult(discarded, removed, problems);
    }
}
