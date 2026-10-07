using Familiar.Core.Images;

namespace Familiar.Core.Tests.Images;

public sealed class WorkingFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));

    public WorkingFoldersTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Create_MakesAMarkedFolderNamedForTheBuildTime()
    {
        var folder = WorkingFolders.Create(_root, new DateTime(2026, 10, 6, 14, 5, 9, DateTimeKind.Local));

        Assert.Equal(Path.Combine(_root, "20261006-140509"), folder);
        Assert.True(WorkingFolders.IsWorkingFolder(folder));
    }

    [Fact]
    public void Clean_RemovesFamiliarsFolders_LeavesEverythingElse()
    {
        var build = WorkingFolders.Create(_root, DateTime.Now);
        File.WriteAllBytes(Path.Combine(build, "install.wim"), [1]);
        var unrelated = Directory.CreateDirectory(Path.Combine(_root, "Projects")).FullName;
        File.WriteAllBytes(Path.Combine(unrelated, "keep.txt"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "notes.txt"), [1]);

        var result = WorkingFolders.Clean(_root, new FakeMounts());

        Assert.Equal([build], result.Removed);
        Assert.Empty(result.Problems);
        Assert.False(Directory.Exists(build));
        Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "notes.txt")));
    }

    [Fact]
    public void Clean_FolderLookingLikeABuildButUnmarked_IsLeftAlone()
    {
        var lookalike = Directory.CreateDirectory(Path.Combine(_root, "20261006-140509")).FullName;

        var result = WorkingFolders.Clean(_root, new FakeMounts());

        Assert.Empty(result.Removed);
        Assert.True(Directory.Exists(lookalike));
    }

    [Fact]
    public void Clean_DiscardsOnlyMountsInsideFamiliarsFolders()
    {
        var build = WorkingFolders.Create(_root, DateTime.Now);
        var ours = Path.Combine(build, "mount");
        var otherTool = Path.Combine(_root, "Projects", "mount");
        var elsewhere = @"D:\Mount";
        var mounts = new FakeMounts(ours, Path.Combine(build, "winre-mount"), otherTool, elsewhere);

        var result = WorkingFolders.Clean(_root, mounts);

        Assert.Equal([Path.Combine(build, "winre-mount"), ours], result.Discarded);
        Assert.Equal([otherTool, elsewhere], mounts.Mounted);
        Assert.Equal([build], result.Removed);
    }

    [Fact]
    public void Clean_NeverDeletesAFolderThatIsStillMounted()
    {
        var build = WorkingFolders.Create(_root, DateTime.Now);
        var mount = Path.Combine(build, "mount");
        Directory.CreateDirectory(mount);
        File.WriteAllBytes(Path.Combine(mount, "Windows.txt"), [1]);
        var mounts = new FakeMounts(mount) { FailDiscard = true };

        var result = WorkingFolders.Clean(_root, mounts);

        Assert.Empty(result.Removed);
        Assert.Contains(result.Problems, p => p.Contains("Could not discard", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("still mounted", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(mount, "Windows.txt")));
    }

    [Fact]
    public void Clean_ADriveRootDeletesNothingFamiliarDidNotCreate()
    {
        // Stands in for '--working-directory D:\': only marked folders may go.
        Directory.CreateDirectory(Path.Combine(_root, "Users"));
        Directory.CreateDirectory(Path.Combine(_root, "Windows"));
        var mounts = new FakeMounts(Path.Combine(_root, "Windows", "mount"));

        var result = WorkingFolders.Clean(_root, mounts);

        Assert.Empty(result.Removed);
        Assert.Empty(result.Discarded);
        Assert.Equal(2, Directory.GetDirectories(_root).Length);
    }

    [Fact]
    public void Clean_MissingWorkingDirectory_DoesNothing()
    {
        var result = WorkingFolders.Clean(Path.Combine(_root, "none"), new FakeMounts());

        Assert.Empty(result.Removed);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void MountsInside_DoesNotMatchASiblingWithTheSamePrefix()
    {
        var mounts = new FakeMounts(Path.Combine(_root, "build-2", "mount"));

        Assert.Empty(WorkingFolders.MountsInside(Path.Combine(_root, "build"), mounts));
        Assert.Single(WorkingFolders.MountsInside(Path.Combine(_root, "build-2"), mounts));
    }

    private sealed class FakeMounts(params string[] mounted) : IMountTable
    {
        public List<string> Mounted { get; } = [.. mounted];

        public bool FailDiscard { get; init; }

        public IReadOnlyList<MountedImageInfo> GetMountedImages() =>
            Mounted.Select(m => new MountedImageInfo(m, @"C:\x\install.wim", "Ok")).ToList();

        public void Discard(string mountPath)
        {
            if (FailDiscard)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            Mounted.Remove(mountPath);
        }

        public void CleanupMountpoints()
        {
        }
    }
}
