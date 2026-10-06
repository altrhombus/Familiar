using System.Globalization;
using System.Text.RegularExpressions;

namespace Familiar.Core.ConfigMgr;

/// <summary>
/// Expands package source folder names such as <c>{release}_{arch}_{date:yyMM}_{n}</c> into
/// <c>25H2_AMD64_2610_1</c>, using the tokens of <see cref="PackageTemplate"/> plus <c>{n}</c>, which counts up
/// from 1 until the folder does not exist yet, so every build gets its own folder and earlier ones stay untouched.
/// </summary>
public static partial class SourceFolderTemplate
{
    /// <param name="exists">Tells whether a candidate folder (full path) is taken; defaults to the file system.</param>
    public static string Expand(string template, string parent, ImageFacts? facts, DateTime date, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        exists ??= Directory.Exists;

        var problems = PackageTemplate.Validate(template, allowCounter: true);
        if (problems.Count > 0)
        {
            throw new ConfigMgrException($"Source folder template: {string.Join("; ", problems)}.");
        }

        var name = PackageTemplate.Expand(template, facts, date);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ConfigMgrException($"The source folder template produced an invalid folder name: {name}");
        }

        if (!CounterPattern().IsMatch(name))
        {
            var fixedPath = Path.Combine(parent, name);
            return exists(fixedPath)
                ? throw new ConfigMgrException($"{fixedPath} already exists. Add {{n}} to the source folder template, or remove the folder.")
                : fixedPath;
        }

        for (var n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(parent, CounterPattern().Replace(name, n.ToString(CultureInfo.InvariantCulture)));
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new ConfigMgrException($"Could not find a free folder name for {name} under {parent}.");
    }

    [GeneratedRegex(@"\{n\}", RegexOptions.IgnoreCase)]
    private static partial Regex CounterPattern();
}
