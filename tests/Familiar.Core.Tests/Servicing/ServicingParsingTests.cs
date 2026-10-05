using Familiar.Core.Servicing;

namespace Familiar.Core.Tests.Servicing;

public class ServicingParsingTests
{
    [Theory]
    [InlineData("[===                        6.0%                           ]", 6.0)]
    [InlineData("[==========================100.0%==========================]", 100.0)]
    [InlineData("  [                           0.0%                           ] ", 0.0)]
    public void DismProgress_ParsesProgressBar(string line, double expected)
    {
        Assert.True(DismCommand.TryParsePercent(line, out var percent));
        Assert.Equal(expected, percent);
    }

    [Theory]
    [InlineData("The operation completed successfully.")]
    [InlineData("Image Version: 10.0.26100.1")]
    [InlineData("Error: 87 [50%]")]
    public void DismProgress_IgnoresOtherOutput(string line)
    {
        Assert.False(DismCommand.TryParsePercent(line, out _));
    }

    [Fact]
    public void PackageIdentity_ParsesLanguagePack()
    {
        Assert.True(PackageIdentity.TryParse("Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~10.0.26100.1", out var identity));

        Assert.Equal(PackageIdentity.ClientLanguagePack, identity.Name);
        Assert.Equal("amd64", identity.Architecture);
        Assert.Equal("de-DE", identity.Language);
        Assert.Equal(new Version(10, 0, 26100, 1), identity.Version);
    }

    [Fact]
    public void PackageIdentity_NeutralPackageHasEmptyLanguage()
    {
        Assert.True(PackageIdentity.TryParse("Microsoft-Windows-Foundation-Package~31bf3856ad364e35~amd64~~10.0.26100.1", out var identity));

        Assert.Equal(string.Empty, identity.Language);
        Assert.Equal(26100, identity.Version?.Build);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Package_for_KB5000000")]
    [InlineData("a~b~c")]
    public void PackageIdentity_RejectsOtherShapes(string value)
    {
        Assert.False(PackageIdentity.TryParse(value, out _));
    }

    [Fact]
    public void CabManifest_ReadsAssemblyIdentity()
    {
        // Trimmed from Microsoft-Windows-Client-Language-Pack_x64_de-de.cab on the 24H2/25H2 media.
        const string manifest = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v3" manifestVersion="1.0">
              <assemblyIdentity name="Microsoft-Windows-Client-LanguagePack-Package" version="10.0.26100.1" processorArchitecture="amd64" language="de-DE" buildType="release" publicKeyToken="31bf3856ad364e35" />
              <package identifier="Client LanguagePack Package" releaseType="Language Pack">
                <parent><assemblyIdentity name="Microsoft-Windows-Foundation-Package" version="10.0.26100.1" processorArchitecture="amd64" language="neutral" publicKeyToken="31bf3856ad364e35" /></parent>
              </package>
            </assembly>
            """;

        var identity = CabManifest.ParseIdentity(manifest);

        Assert.Equal("Microsoft-Windows-Client-LanguagePack-Package", identity?.Name);
        Assert.Equal(26100, identity?.Version?.Build);
        Assert.Equal("de-DE", identity?.Language);
    }
}
