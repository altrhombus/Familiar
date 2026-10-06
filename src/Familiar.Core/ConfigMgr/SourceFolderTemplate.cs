using System.Globalization;
using System.Text.RegularExpressions;

namespace Familiar.Core.ConfigMgr;

/// <summary>
/// Expands package source folder names such as <c>{release}_{arch}_{date:yyMM}_{n}</c> into
/// <c>25H2_AMD64_2610_1</c>. <c>{n}</c> counts up from 1 until the folder does not exist yet, so every build gets
/// its own folder and earlier ones stay untouched for rollback.
/// </summary>
public static partial class SourceFolderTemplate
{
    /// <summary>True when the template needs image details (release or architecture).</summary>
    public static bool NeedsImageDetails(string template) =>
        template.Contains("{release}", StringComparison.OrdinalIgnoreCase) || template.Contains("{arch}", StringComparison.OrdinalIgnoreCase);

    /// <param name="exists">Tells whether a candidate folder (full path) is taken; defaults to the file system.</param>
    public static string Expand(string template, string parent, string? release, string? architecture, DateTime date, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        exists ??= Directory.Exists;

        var name = TokenPattern().Replace(template, match =>
        {
            var token = match.Groups["name"].Value.ToLowerInvariant();
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
            return token switch
            {
                "release" => release ?? throw new ConfigMgrException("The source folder template uses {release}, but the image's release is unknown."),
                "arch" => architecture ?? throw new ConfigMgrException("The source folder template uses {arch}, but the image's architecture is unknown."),
                "date" => date.ToString(format ?? "yyyy.MM.dd", CultureInfo.InvariantCulture),
                "n" => match.Value,
                _ => throw new ConfigMgrException($"Unknown token {match.Value} in the source folder template. Use {{release}}, {{arch}}, {{date:format}} or {{n}}."),
            };
        });

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ConfigMgrException($"The source folder template produced an invalid folder name: {name}");
        }

        if (!name.Contains("{n}", StringComparison.OrdinalIgnoreCase))
        {
            var fixedPath = Path.Combine(parent, name);
            return exists(fixedPath)
                ? throw new ConfigMgrException($"{fixedPath} already exists. Add {{n}} to the source folder template, or remove the folder.")
                : fixedPath;
        }

        for (var n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(parent, NumberPattern().Replace(name, n.ToString(CultureInfo.InvariantCulture)));
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new ConfigMgrException($"Could not find a free folder name for {name} under {parent}.");
    }

    [GeneratedRegex(@"\{(?<name>[a-zA-Z]+)(:(?<format>[^}]+))?\}")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\{n\}", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();
}
