using System.Diagnostics;
using System.Xml.Linq;

namespace Familiar.Core.Servicing;

public static class CabManifest
{
    /// <summary>
    /// Reads the package identity from a servicing cab's <c>update.mum</c>, e.g. a language pack built for
    /// 10.0.26100.1. Used to confirm media matches an image before anything is applied.
    /// </summary>
    public static async Task<PackageIdentity?> ReadIdentityAsync(string cabPath, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "familiar-mum-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add("-F:update.mum");
            startInfo.ArgumentList.Add(cabPath);
            startInfo.ArgumentList.Add(folder);

            using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("expand.exe did not start."))
            {
                await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            var manifest = Path.Combine(folder, "update.mum");
            return File.Exists(manifest) ? ParseIdentity(await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false)) : null;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    internal static PackageIdentity? ParseIdentity(string manifestXml)
    {
        var identity = XDocument.Parse(manifestXml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "assemblyIdentity");
        if (identity is null)
        {
            return null;
        }

        return new PackageIdentity(
            (string?)identity.Attribute("name") ?? string.Empty,
            (string?)identity.Attribute("publicKeyToken") ?? string.Empty,
            (string?)identity.Attribute("processorArchitecture") ?? string.Empty,
            (string?)identity.Attribute("language") ?? string.Empty,
            Version.TryParse((string?)identity.Attribute("version"), out var version) ? version : null);
    }
}
