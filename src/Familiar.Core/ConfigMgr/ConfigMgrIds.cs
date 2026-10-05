using System.Text.RegularExpressions;

namespace Familiar.Core.ConfigMgr;

/// <summary>
/// Validates identifiers before they go into WQL or OData URLs, so a malformed recipe value can never change
/// the meaning of a query.
/// </summary>
internal static partial class ConfigMgrIds
{
    public static string Package(string value) =>
        PackagePattern().IsMatch(value) ? value.ToUpperInvariant() : throw new ConfigMgrException($"'{value}' is not a PackageID (for example PS100123).");

    public static string SiteCode(string value) =>
        SitePattern().IsMatch(value) ? value.ToUpperInvariant() : throw new ConfigMgrException($"'{value}' is not a site code.");

    public static string Group(string value) =>
        GroupPattern().IsMatch(value) ? value : throw new ConfigMgrException($"'{value}' is not a distribution point group ID.");

    [GeneratedRegex("^[A-Za-z0-9]{3}[0-9A-Fa-f]{5}$")]
    private static partial Regex PackagePattern();

    [GeneratedRegex("^[A-Za-z0-9]{3}$")]
    private static partial Regex SitePattern();

    [GeneratedRegex(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$")]
    private static partial Regex GroupPattern();
}
