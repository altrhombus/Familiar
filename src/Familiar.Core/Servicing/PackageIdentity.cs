namespace Familiar.Core.Servicing;

/// <summary>
/// A CBS package identity such as
/// <c>Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~10.0.26100.1</c>.
/// </summary>
public sealed record PackageIdentity(string Name, string PublicKeyToken, string Architecture, string Language, Version? Version)
{
    public const string FoundationPackage = "Microsoft-Windows-Foundation-Package";
    public const string ClientLanguagePack = "Microsoft-Windows-Client-LanguagePack-Package";

    public static bool TryParse(string? value, out PackageIdentity identity)
    {
        identity = null!;
        var parts = value?.Split('~');
        if (parts is not { Length: 5 } || parts[0].Length == 0)
        {
            return false;
        }

        identity = new PackageIdentity(
            parts[0],
            parts[1],
            parts[2],
            parts[3],
            Version.TryParse(parts[4], out var version) ? version : null);
        return true;
    }

    public override string ToString() => $"{Name}~{PublicKeyToken}~{Architecture}~{Language}~{Version}";
}
