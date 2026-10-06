using System.Globalization;
using System.Text.RegularExpressions;

namespace Familiar.Core.ConfigMgr;

/// <summary>Facts about a built image that package names, versions, descriptions and folders can use.</summary>
/// <param name="Release">Feature release, e.g. 25H2.</param>
/// <param name="Architecture">AMD64 or ARM64.</param>
/// <param name="Edition">DISM EditionId, e.g. Enterprise.</param>
/// <param name="Build">Build and revision, e.g. 26200.9457.</param>
/// <param name="Kb">Cumulative update the image is at, e.g. KB5129195.</param>
/// <param name="UpdateDate">Release date of that update.</param>
public sealed record ImageFacts(string? Release, string? Architecture, string? Edition, string? Build, string? Kb, DateOnly? UpdateDate);

/// <summary>
/// Expands text such as <c>Windows 11 {arch}</c>, <c>{release}</c> or <c>{cu:yyyy-MM}</c> for ConfigMgr fields.
/// Sites name things differently, so every field is a template rather than a fixed format.
/// </summary>
public static partial class PackageTemplate
{
    /// <summary>Tokens and what they mean, for help text and error messages.</summary>
    public const string TokenHelp =
        "{release} (25H2), {arch} (AMD64), {edition} (Enterprise), {build} (26200.9457), {kb} (KB5129195), "
        + "{cu:format} (date of that update, default yyyy-MM), {date:format} (publish date, default yyyy.MM.dd)";

    private static readonly string[] ImageTokens = ["release", "arch", "edition", "build", "kb", "cu"];

    /// <summary>Problems with a template's tokens; empty when valid.</summary>
    /// <param name="allowCounter">Allow <c>{n}</c>, which only folder names use.</param>
    public static IReadOnlyList<string> Validate(string template, bool allowCounter = false)
    {
        var problems = new List<string>();
        foreach (Match token in TokenPattern().Matches(template))
        {
            var name = token.Groups["name"].Value.ToLowerInvariant();
            if (name == "n" ? !allowCounter : name is not ("date" or "cu") && !ImageTokens.Contains(name))
            {
                problems.Add($"unknown token {token.Value}; use {TokenHelp}{(allowCounter ? " or {n}" : string.Empty)}");
            }
            else if (token.Groups["format"].Success && name is not ("date" or "cu"))
            {
                problems.Add($"{token.Value} takes no format");
            }
        }

        return problems;
    }

    /// <summary>True when the template needs facts read from the image (which takes the DISM API).</summary>
    public static bool NeedsImageFacts(string? template) =>
        template is not null && TokenPattern().Matches(template).Any(t => ImageTokens.Contains(t.Groups["name"].Value.ToLowerInvariant()));

    /// <summary>Expands every token; <c>{n}</c> is left in place for <see cref="SourceFolderTemplate"/> to number.</summary>
    public static string Expand(string template, ImageFacts? facts, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(template);

        return TokenPattern().Replace(template, token =>
        {
            var name = token.Groups["name"].Value.ToLowerInvariant();
            var format = token.Groups["format"].Success ? token.Groups["format"].Value : null;
            return name switch
            {
                "n" => token.Value,
                "date" => now.ToString(format ?? "yyyy.MM.dd", CultureInfo.InvariantCulture),
                "cu" => (facts?.UpdateDate ?? throw Unknown(token.Value, "the cumulative update the image is at"))
                    .ToString(format ?? "yyyy-MM", CultureInfo.InvariantCulture),
                "release" => facts?.Release ?? throw Unknown(token.Value, "the image's release"),
                "arch" => facts?.Architecture ?? throw Unknown(token.Value, "the image's architecture"),
                "edition" => facts?.Edition ?? throw Unknown(token.Value, "the image's edition"),
                "build" => facts?.Build ?? throw Unknown(token.Value, "the image's build"),
                "kb" => facts?.Kb ?? throw Unknown(token.Value, "the cumulative update the image is at"),
                _ => throw new ConfigMgrException($"Unknown token {token.Value}. Use {TokenHelp}."),
            };
        });
    }

    private static ConfigMgrException Unknown(string token, string what) =>
        new($"{token} needs {what}, which could not be determined (the release data may not list this build; try --refresh).");

    [GeneratedRegex(@"\{(?<name>[a-zA-Z]+)(:(?<format>[^}]+))?\}")]
    private static partial Regex TokenPattern();
}
