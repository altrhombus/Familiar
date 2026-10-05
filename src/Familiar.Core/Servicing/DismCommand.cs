using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Familiar.Core.Servicing;

/// <summary>Result of a dism.exe run.</summary>
public sealed record DismCommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode is 0 or ErrorSuccessRebootRequired;

    // 3010: the operation succeeded and a reboot is required, which is irrelevant for offline images.
    private const int ErrorSuccessRebootRequired = 3010;
}

/// <summary>
/// Runs dism.exe for operations the DISM API does not expose: international settings, lang.ini,
/// image export and component cleanup.
/// </summary>
public sealed partial class DismCommand(string? dismPath = null)
{
    private readonly string _dismPath = dismPath ?? Path.Combine(Environment.SystemDirectory, "dism.exe");

    /// <summary>Sets UI language, locale, input and time zone defaults to one language.</summary>
    public Task<DismCommandResult> SetAllInternationalAsync(string mountPath, string language, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync($"Set default language {language}", ["/Image:" + mountPath, "/Set-AllIntl:" + language], progress, cancellationToken);

    /// <summary>Sets the language a user sees when Windows first starts (before OOBE picks one).</summary>
    public Task<DismCommandResult> SetSkuDefaultsAsync(string mountPath, string language, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync($"Set SKU defaults {language}", ["/Image:" + mountPath, "/Set-SKUIntlDefaults:" + language], progress, cancellationToken);

    /// <summary>Regenerates Setup's lang.ini so installation media offers every language in the image.</summary>
    public Task<DismCommandResult> GenerateLangIniAsync(string mountPath, string distributionPath, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync("Generate lang.ini", ["/Image:" + mountPath, "/Gen-LangINI", "/Distribution:" + distributionPath], progress, cancellationToken);

    /// <summary>Removes superseded components; with <paramref name="resetBase"/>, updates can no longer be uninstalled.</summary>
    public Task<DismCommandResult> CleanupComponentsAsync(string mountPath, bool resetBase, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync("Clean up components", ["/Image:" + mountPath, "/Cleanup-Image", "/StartComponentCleanup", .. resetBase ? ["/ResetBase"] : Array.Empty<string>()], progress, cancellationToken);

    /// <summary>
    /// Adds a package with dism.exe. Used for .msu updates: since 24H2 they install through an update agent in a
    /// separate process that needs the image's servicing session to itself, which fails (0x800401E3) while a
    /// DISM API session is open. <paramref name="scratchPath"/> keeps the expanded update out of the image.
    /// </summary>
    public Task<DismCommandResult> AddPackageAsync(string mountPath, string packagePath, string scratchPath, string operation, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(scratchPath);
        return RunAsync(operation, ["/Image:" + mountPath, "/Add-Package", "/PackagePath:" + packagePath, "/ScratchDir:" + scratchPath], progress, cancellationToken);
    }

    /// <summary>Copies one index into another WIM, recompressing it. Also converts ESD sources to WIM.</summary>
    public Task<DismCommandResult> ExportImageAsync(string sourceImage, int sourceIndex, string destinationImage, string? destinationName = null, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        List<string> arguments =
        [
            "/Export-Image",
            "/SourceImageFile:" + sourceImage,
            "/SourceIndex:" + sourceIndex.ToString(CultureInfo.InvariantCulture),
            "/DestinationImageFile:" + destinationImage,
            "/Compress:max",
            "/CheckIntegrity",
        ];

        if (!string.IsNullOrWhiteSpace(destinationName))
        {
            arguments.Add("/DestinationName:" + destinationName);
        }

        return RunAsync("Export image", arguments, progress, cancellationToken);
    }

    public async Task<DismCommandResult> RunAsync(string operation, IEnumerable<string> arguments, IProgress<ServicingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(_dismPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // English output keeps progress parsing and logs consistent regardless of the host language.
        startInfo.ArgumentList.Add("/English");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        progress?.Report(ServicingProgress.Started(operation));

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dism.exe did not start.");
        var output = new StringBuilder();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            // DISM redraws its progress bar with carriage returns, so read characters rather than lines.
            var buffer = new char[512];
            var line = new StringBuilder();
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        Flush();
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }

            Flush();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            void Flush()
            {
                if (line.Length == 0)
                {
                    return;
                }

                var text = line.ToString();
                line.Clear();
                if (TryParsePercent(text, out var percent))
                {
                    progress?.Report(new ServicingProgress(operation, percent));
                }
                else
                {
                    output.AppendLine(text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Killing dism.exe mid-operation leaves the image to be discarded by the caller; it does not corrupt the source.
            process.Kill(entireProcessTree: true);
            throw;
        }

        output.Append(await errorTask.ConfigureAwait(false));
        return new DismCommandResult(process.ExitCode, output.ToString());
    }

    /// <summary>Parses a DISM progress bar line such as <c>[=====   12.3%   ]</c>.</summary>
    internal static bool TryParsePercent(string line, out double percent)
    {
        var match = ProgressPattern().Match(line);
        percent = match.Success ? double.Parse(match.Groups["percent"].Value, CultureInfo.InvariantCulture) : 0;
        return match.Success;
    }

    [GeneratedRegex(@"^\s*\[[=\s]*(?<percent>\d{1,3}(?:\.\d+)?)%[=\s]*\]\s*$")]
    private static partial Regex ProgressPattern();
}
