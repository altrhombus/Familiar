using Familiar.Core.Servicing;
using Microsoft.Win32;

namespace Familiar.Core.Tests.Servicing;

public class OfflineRegistryTests
{
    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\{bf1a281b-ad7b-4476-ac95-f47682990ce7}SOFTWARE", @"{bf1a281b-ad7b-4476-ac95-f47682990ce7}SOFTWARE")]
    [InlineData(@"HKLM\OFFLINE_SOFTWARE", "OFFLINE_SOFTWARE")]
    [InlineData(@"\Registry\Machine\{guid}SOFTWARE\", "{guid}SOFTWARE")]
    [InlineData("{guid}SOFTWARE", "{guid}SOFTWARE")]
    public void ParseMountPoint_AcceptsWin32AndNativeForms(string mountPoint, string expectedPath)
    {
        var (root, path) = OfflineRegistry.ParseMountPoint(mountPoint);

        Assert.Same(Registry.LocalMachine, root);
        Assert.Equal(expectedPath, path);
    }

    [Fact]
    public void ParseMountPoint_UsersHive()
    {
        var (root, path) = OfflineRegistry.ParseMountPoint(@"\Registry\User\{guid}DEFAULT");

        Assert.Same(Registry.Users, root);
        Assert.Equal("{guid}DEFAULT", path);
    }

    [Fact]
    public void ParseMountPoint_RejectsUnknownRoots()
    {
        Assert.Throws<FormatException>(() => OfflineRegistry.ParseMountPoint(@"HKEY_CLASSES_ROOT\x"));
    }
}
