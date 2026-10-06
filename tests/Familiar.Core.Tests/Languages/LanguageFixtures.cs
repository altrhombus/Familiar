namespace Familiar.Core.Tests.Languages;

/// <summary>Builds an empty-file replica of the Windows 11 24H2/25H2 Languages and Optional Features ISO layout.</summary>
internal static class LanguageFixtures
{
    private static readonly string[] FeatureFiles =
    [
        "Microsoft-Windows-Client-Language-Pack_x64_de-de.cab",
        "Microsoft-Windows-Client-Language-Pack_x64_ja-jp.cab",
        "Microsoft-Windows-Client-Language-Pack_x64_sr-latn-rs.cab",
        "Microsoft-Windows-Client-Language-Pack_arm64_fr-fr.cab",
        "Microsoft-Windows-Lip-Language-Pack_x64_ca-es.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-de-de-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Handwriting-de-de-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-OCR-de-de-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Speech-de-de-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-TextToSpeech-de-de-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-de-de-Package~31bf3856ad364e35~arm64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-ja-jp-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-OCR-ja-jp-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-sr-latn-rs-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-ca-es-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-af-za-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-de-ch-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-zh-cn-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Basic-zh-tw-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Fonts-Jpan-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Fonts-Hans-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Fonts-Hant-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-LanguageFeatures-Fonts-PanEuropeanSupplementalFonts-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft-Windows-Notepad-FoD-Package~31bf3856ad364e35~amd64~de-DE~.cab",
        "Microsoft-Windows-Notepad-FoD-Package~31bf3856ad364e35~wow64~de-DE~.cab",
        "FoDMetadata_Client.cab",
    ];

    private static readonly string[] WinPEFiles =
    [
        @"de-de\lp.cab",
        @"de-de\winpe-setup_de-de.cab",
        @"de-de\winpe-setup-client_de-de.cab",
        @"de-de\winpe-winrecfg_de-de.cab",
        @"ja-jp\lp.cab",
        @"ja-jp\winpe-setup_ja-jp.cab",
        "WinPE-FontSupport-JA-JP.cab",
        "WinPE-Speech-TTS-de-DE.cab",
    ];

    public static string CreateIso(string root)
    {
        Touch(Path.Combine(root, "LanguagesAndOptionalFeatures"), FeatureFiles);
        Touch(Path.Combine(root, "Windows Preinstallation Environment", "x64", "WinPE_OCs"), WinPEFiles);
        return root;
    }

    private static void Touch(string folder, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }
    }
}
