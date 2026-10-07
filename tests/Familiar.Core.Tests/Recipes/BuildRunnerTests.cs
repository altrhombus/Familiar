using Familiar.Core.Diagnostics;
using Familiar.Core.Languages;
using Familiar.Core.Recipes;
using Familiar.Core.Releases;
using Familiar.Core.Servicing;
using Familiar.Core.Tests.Languages;
using Familiar.Core.Tests.Releases;

namespace Familiar.Core.Tests.Recipes;

/// <summary>
/// The build's decisions (order, skips, warnings, failure handling) against <see cref="FakeImages"/>, so they run
/// without DISM, elevation or Windows media. The rules tested here came from real builds; see the commit history.
/// </summary>
public sealed class BuildRunnerTests : IDisposable
{
    private const string CumulativeUpdate = "windows11.0-kb5129195-x64_0123456789abcdef0123456789abcdef01234567.msu";
    private const string DotNetUpdate = "windows11.0-kb5129999-x64-ndp481_aaaaaaaa.msu";
    private const string SafeOSUpdate = "windows11.0-kb5125758-x64_8dd60b2db4ab21f796b5f8d510067edce6827104.cab";

    private static readonly ReleaseCatalog Catalog = TestData.Catalog(
        releases: """
            { "os_type": "client", "major_version": 11, "windows_version": "24H2", "os_build": "26100" },
            { "os_type": "client", "major_version": 11, "windows_version": "25H2", "os_build": "26200" }
            """,
        builds: """
            { "build": "26100.6584", "os_type": "client", "major_version": 11, "windows_version": "24H2", "release_date": "2025-09-09", "kb_article": "KB5065426", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26200.6584", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2025-09-09", "kb_article": "KB5065426", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26100.9457", "os_type": "client", "major_version": 11, "windows_version": "24H2", "release_date": "2026-09-14", "kb_article": "KB5129195", "release_type": "Standard", "is_expired": false, "article_url": null },
            { "build": "26200.9457", "os_type": "client", "major_version": 11, "windows_version": "25H2", "release_date": "2026-09-14", "kb_article": "KB5129195", "release_type": "Standard", "is_expired": false, "article_url": null }
            """);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _output;
    private readonly string _work;
    private readonly string _updates;
    private readonly List<string> _log = [];

    public BuildRunnerTests()
    {
        _source = Touch(Path.Combine(_root, "media", "sources", "install.wim"));
        _output = Path.Combine(_root, "output", "install-de.wim");
        _work = Path.Combine(_root, "work");
        _updates = Path.Combine(_root, "updates");
        Touch(Path.Combine(_updates, CumulativeUpdate));
        Touch(Path.Combine(_updates, DotNetUpdate));
        Touch(Path.Combine(_updates, UpdatesSection.SafeOSFolderName, SafeOSUpdate));
        LanguageFixtures.CreateIso(Path.Combine(_root, "languages"));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Build_RunsEveryStepInMicrosoftsOrder()
    {
        var images = Images();

        var result = await Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "export source -> install.wim",
                "image mount",

                // WinRE first: the cumulative update for its servicing stack, then the Safe OS Dynamic Update.
                "winre mount",
                "winre dism-add " + CumulativeUpdate,
                "winre apply " + SafeOSUpdate,
                "winre cleanup /ResetBase",
                "winre commit",
                "export winre.wim -> winre-final.wim",

                // Languages before updates, so the cumulative update also services the new language resources.
                "image apply Microsoft-Windows-Client-Language-Pack_x64_de-de.cab",
                "image capability Language.Basic~~~de-DE~0.0.1.0",
                "image set-all-intl de-DE",

                // .msu updates go through dism.exe; .NET after Windows.
                "image dism-add " + CumulativeUpdate,
                "image dism-add " + DotNetUpdate,
                "image cleanup",
                "image commit",
                "export install.wim -> output as Windows 11 Enterprise de-DE",
            ],
            images.Calls);
        Assert.Equal(_output, result.OutputPath);
        Assert.True(File.Exists(_output));
        Assert.Empty(Directory.GetDirectories(_work));
    }

    [Fact]
    public async Task Build_PutsTheServicedWinREBackWithItsAttributes()
    {
        var images = Images();

        await Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(images.ServicedWinREContent, images.CommittedWinRE!.Value.Content);
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System, images.CommittedWinRE.Value.Attributes);
        Assert.Contains(_log, l => l.Contains("WinRE: build 26100.5059 -> 26100.9545", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_LanguageMediaForAnotherBuild_FailsBeforeChangingTheImage()
    {
        var images = Images();
        var runner = Runner(images, mediaBuild: 22621);

        var ex = await Assert.ThrowsAsync<BuildException>(() => runner.RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("build 22621", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["export source -> install.wim", "image mount", "image discard"], images.Calls);
        Assert.False(File.Exists(_output));
        Assert.Empty(Directory.GetDirectories(_work));
    }

    [Fact]
    public async Task Build_FailedUpdate_DiscardsTheImageAndLeavesNoOutput()
    {
        var images = Images();
        images.AddUpdateExitCodes[DotNetUpdate] = unchecked((int)0x800F0922);

        var ex = await Assert.ThrowsAsync<BuildException>(() => Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("dism.exe failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal("image discard", images.Calls[^1]);
        Assert.DoesNotContain("image commit", images.Calls);
        Assert.False(File.Exists(_output));
        Assert.Empty(Directory.GetDirectories(_work));
    }

    [Fact]
    public async Task Build_Cancelled_DiscardsTheImage()
    {
        using var cancellation = new CancellationTokenSource();
        var images = Images(onApply: call =>
        {
            if (call.Contains("Language-Pack", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: cancellation.Token));

        Assert.Equal("image discard", images.Calls[^1]);
        Assert.DoesNotContain(images.Calls, c => c.Contains("set-all-intl", StringComparison.Ordinal));
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public async Task Build_WhenTheDiscardFails_KeepsTheWorkingFolderForCleanup()
    {
        var images = Images(failDiscard: true);
        images.AddUpdateExitCodes[DotNetUpdate] = 1;

        await Assert.ThrowsAnyAsync<Exception>(() => Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken));

        var folder = Assert.Single(Directory.GetDirectories(_work));
        Assert.True(Familiar.Core.Images.WorkingFolders.IsWorkingFolder(folder));
        Assert.Contains(_log, l => l.Contains("familiar cleanup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_ExistingOutput_IsRefusedBeforeAnythingStarts()
    {
        Touch(_output);
        var images = Images();

        var ex = await Assert.ThrowsAsync<BuildException>(() => Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.Empty(images.Calls);
        Assert.False(Directory.Exists(_work));
    }

    [Fact]
    public async Task Build_UnreadableRegistry_IsAWarning()
    {
        var images = Images(registryUnreadable: true);

        var result = await Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("registry version", StringComparison.Ordinal));
        Assert.True(File.Exists(_output));
    }

    [Fact]
    public async Task Build_FailureWithDefenderBlocks_NamesTheRule()
    {
        var images = Images();
        images.AddUpdateExitCodes[CumulativeUpdate] = unchecked((int)0x800F0922);
        var block = new AsrBlock(DateTime.Now, new Guid("56a863a9-875e-4185-98a7-b882c64b5ce5"), @"C:\FamiliarWork\x\mount\Windows\System32\drivers\ksld.sys", @"C:\Windows\System32\Dism\DismHost.exe");
        var runner = Runner(images, asrBlocks: [block]);

        var ex = await Assert.ThrowsAsync<BuildException>(() => runner.RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("attack surface reduction", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ksld.sys", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WinRE_KnownIssueWithCombinedUpdates_IsTolerated()
    {
        var images = Images();
        images.AddUpdateExitCodes["winre:" + CumulativeUpdate] = unchecked((int)0x8007007E);

        var result = await Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("winre apply " + SafeOSUpdate, images.Calls);
        Assert.Contains("winre commit", images.Calls);
        Assert.Contains(_log, l => l.Contains("known issue", StringComparison.Ordinal));
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task WinRE_OtherUpdateFailure_StopsTheBuild()
    {
        var images = Images();
        images.AddUpdateExitCodes["winre:" + CumulativeUpdate] = 5;

        var ex = await Assert.ThrowsAsync<BuildException>(() => Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("to WinRE", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["winre discard", "image discard"], images.Calls[^2..]);
    }

    [Fact]
    public async Task WinRE_WithoutSafeOSUpdate_IsLeftAloneWithAWarning()
    {
        Directory.Delete(Path.Combine(_updates, UpdatesSection.SafeOSFolderName), recursive: true);
        var images = Images();

        var result = await Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("WinRE was not patched", StringComparison.Ordinal));
        Assert.DoesNotContain(images.Calls, c => c.StartsWith("winre", StringComparison.Ordinal));
        Assert.Equal(images.WinREContent, images.CommittedWinRE!.Value.Content);
    }

    [Fact]
    public async Task WinRE_MissingFromTheImage_IsAWarning()
    {
        var images = Images(withWinRE: false);

        var result = await Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("no WinRE", StringComparison.Ordinal));
        Assert.DoesNotContain(images.Calls, c => c.StartsWith("winre", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WinRE_StillAtTheSameRevisionAfterPatching_IsAWarning()
    {
        var images = Images();
        images.WinREAfter = images.WinREBefore;

        var result = await Runner(images).RunAsync(Recipe(), log: _log.Add, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Warnings, w => w.Contains("WinRE is still at build 26100.5059", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WinRE_Localized_IsCheckedForGrowth()
    {
        var images = Images(servicedWinRE: new byte[2 << 20]);
        var recipe = Recipe() with { WinRE = new WinRESection { Languages = ["de-DE"] } };

        var result = await Runner(images).RunAsync(recipe, log: _log.Add, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(images.Calls, c => c.StartsWith("winre apply ", StringComparison.Ordinal) && c.EndsWith("lp.cab", StringComparison.Ordinal));
        Assert.Contains("winre set-all-intl de-DE", images.Calls);
        Assert.Contains(result.Warnings, w => w.Contains("Localizing grew WinRE", StringComparison.Ordinal));
    }

    private FakeImages Images(
        Action<string>? onApply = null, bool failDiscard = false, bool registryUnreadable = false, bool withWinRE = true, byte[]? servicedWinRE = null) =>
        new(_source, _output)
        {
            OnApply = onApply,
            FailDiscard = failDiscard,
            RegistryUnreadable = registryUnreadable,
            WithWinRE = withWinRE,
            ServicedWinREContent = servicedWinRE ?? [2, 2],
        };

    private static BuildRunner Runner(FakeImages images, int mediaBuild = 26100, IReadOnlyList<AsrBlock>? asrBlocks = null) =>
        new(Catalog, downloader: null, images)
        {
            ReadCabIdentity = (path, _) => Task.FromResult<PackageIdentity?>(
                Path.GetFileName(path).Contains("kb5125758", StringComparison.OrdinalIgnoreCase)
                    ? new PackageIdentity("Package_for_SafeOSDU", "31bf3856ad364e35", "amd64", string.Empty, new Version(10, 0, 26100, 9545))
                    : new PackageIdentity(PackageIdentity.ClientLanguagePack, "31bf3856ad364e35", "amd64", "de-DE", new Version(10, 0, mediaBuild, 1))),
            FindAsrBlocks = (_, _) => asrBlocks ?? [],
            Mounts = images,
        };

    private BuildRecipe Recipe() => new()
    {
        Source = new SourceSection { Path = _source, Index = 3 },
        WorkingDirectory = _work,
        Languages = new LanguagesSection
        {
            MediaPath = Path.Combine(_root, "languages"),
            Languages = ["de-DE"],
            Features = [LanguageFeature.Basic],
            IncludeFonts = false,
            DefaultLanguage = "de-DE",
        },
        Updates = new UpdatesSection { Folder = _updates },
        Output = new OutputSection { Path = _output, ImageName = "Windows 11 Enterprise de-DE" },
    };

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }
}
