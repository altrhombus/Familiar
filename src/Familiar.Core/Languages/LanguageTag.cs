using System.Globalization;
using System.Runtime.InteropServices;

namespace Familiar.Core.Languages;

public static partial class LanguageTag
{
    private const uint LocaleScripts = 0x6C; // LOCALE_SSCRIPTS

    /// <summary>Canonical casing for a BCP 47 tag: "sr-latn-rs" becomes "sr-Latn-RS".</summary>
    public static string Normalize(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        try
        {
            var culture = CultureInfo.GetCultureInfo(tag, predefinedOnly: true);
            return culture.Name.Length > 0 ? culture.Name : tag;
        }
        catch (CultureNotFoundException)
        {
            return CanonicalCase(tag);
        }
    }

    /// <summary>English display name, or the tag itself when Windows does not know it.</summary>
    public static string DisplayName(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag, predefinedOnly: true).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    /// <summary>
    /// ISO 15924 scripts Windows associates with a locale (e.g. ja-JP: Hani, Hira, Jpan, Kana), used to pick
    /// font Features on Demand. Empty when the locale is unknown.
    /// </summary>
    public static IReadOnlyList<string> Scripts(string tag)
    {
        Span<char> buffer = stackalloc char[128];
        var length = GetLocaleInfoEx(tag, LocaleScripts, ref MemoryMarshal.GetReference(buffer), buffer.Length);
        if (length <= 1)
        {
            return [];
        }

        return new string(buffer[..(length - 1)]).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string CanonicalCase(string tag)
    {
        var parts = tag.Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = i == 0 ? parts[i].ToLowerInvariant()
                : parts[i].Length == 4 ? char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant()
                : parts[i].ToUpperInvariant();
        }

        return string.Join('-', parts);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetLocaleInfoEx(string localeName, uint lcType, ref char data, int dataLength);
}
