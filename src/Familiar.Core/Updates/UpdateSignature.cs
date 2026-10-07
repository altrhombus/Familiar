using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Familiar.Core.Updates;

/// <summary>
/// Checks that a downloaded update carries a valid Authenticode signature from Microsoft. The catalog's SHA-1 only
/// says the file is the one the catalog page listed; the signature says Microsoft made it. .msu and .cab updates are
/// signed this way.
/// </summary>
public static partial class UpdateSignature
{
    /// <summary>Returns null when <paramref name="path"/> is validly signed by Microsoft, otherwise why it is not.</summary>
    public static string? Check(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var result = Verify(path);
        if (result != 0)
        {
            return result switch
            {
                TrustNoSignature or TrustSubjectFormUnknown => "it has no signature",
                TrustBadDigest => "its signature does not match its content",
                CertUntrustedRoot or CertChaining => "its signature does not chain to a trusted root",
                TrustExplicitDistrust => "its signer is explicitly distrusted",
                _ => $"signature check failed (0x{result:X8})",
            };
        }

        string? organization;
        try
        {
#pragma warning disable SYSLIB0057 // X509CertificateLoader has no way to read the signer of a signed file.
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            organization = new X500DistinguishedName(signer.Subject).EnumerateRelativeDistinguishedNames()
                .FirstOrDefault(n => n.GetSingleElementType().Value == "2.5.4.10")?.GetSingleElementValue();
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return $"its signer could not be read ({ex.Message})";
        }

        return organization == "Microsoft Corporation" ? null : $"it is signed by {organization ?? "an unnamed organization"}, not Microsoft";
    }

    private const int TrustNoSignature = unchecked((int)0x800B0100);
    private const int TrustSubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustBadDigest = unchecked((int)0x80096010);
    private const int CertUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertChaining = unchecked((int)0x800B010A);
    private const int TrustExplicitDistrust = unchecked((int)0x800B0111);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static unsafe int Verify(string path)
    {
        fixed (char* file = path)
        {
            var fileInfo = new WinTrustFileInfo { Size = (uint)sizeof(WinTrustFileInfo), FilePath = (IntPtr)file };
            var data = new WinTrustData
            {
                Size = (uint)sizeof(WinTrustData),
                UIChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE: build machines may be offline, and a revocation fetch can hang
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = (IntPtr)(&fileInfo),
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProviderFlags = 0x10 | 0x1000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(new IntPtr(-1), &action, &data);

            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(new IntPtr(-1), &action, &data);
            return result;
        }
    }

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(IntPtr window, Guid* action, WinTrustData* data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }
}
