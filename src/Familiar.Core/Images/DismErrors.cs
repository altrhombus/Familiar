using System.Runtime.InteropServices;

namespace Familiar.Core.Images;

/// <summary>Readable descriptions for DISM API HRESULTs, which otherwise surface as bare hex codes.</summary>
public static class DismErrors
{
    private static readonly Dictionary<uint, string> Known = new()
    {
        [0xC0040001] = "The DISM API is not initialized.",
        [0xC0040003] = "DISM sessions are still open.",
        [0xC0040004] = "The DISM session is not valid.",
        [0xC0040005] = "The image index does not exist.",
        [0xC0040006] = "The image name does not exist.",
        [0xC0040007] = "The mount folder could not be unmounted.",
        [0xC0040009] = "DISM could not open its log file. Check the --dism-log path is writable.",
        [0xC004000A] = "Files are open in the mount folder, so it could not be unmounted. Close Explorer windows or terminals inside it.",
        [0xC004000B] = "Files are open in the mount folder, so the image could not be mounted.",
        [0xC1510114] = "The image needs to be remounted (the machine may have restarted mid-build). Run 'dism /Cleanup-Mountpoints'.",
        [0x800F081F] = "A package's source files were not found. Check the language or Features on Demand media matches the image.",
        [0x800F0922] = "A package could not be applied; often the media does not match the image build.",
        [0x800F0902] = "DISM is busy with another operation. Wait for it to finish, or close other servicing tools.",
        [0x800F082F] = "The image has pending operations that must be resolved first.",
        [0x800F0952] = "The image has a pending reboot operation from a previous servicing run.",
        [0x800401E3] = "DISM's servicing session for the image was unavailable, usually because another session held it.",
        [0x80070005] = "Access denied. Run from an elevated prompt and check nothing else has the files open.",
        [0x80070070] = "Not enough disk space.",
    };

    /// <summary>Describes a DISM failure, or returns <c>null</c> when the exception is not a DISM error.</summary>
    public static string? Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not (COMException or Microsoft.Dism.DismException))
        {
            return null;
        }

        var code = unchecked((uint)exception.HResult);
        var detail = Known.TryGetValue(code, out var known) ? known : exception.Message;
        return $"DISM error 0x{code:X8}: {detail}";
    }
}
