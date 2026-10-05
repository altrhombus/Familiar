using System.Runtime.InteropServices;
using Familiar.Core.Images;

namespace Familiar.Core.Tests.Servicing;

public class DismErrorsTests
{
    [Fact]
    public void Describe_KnownCode_IsReadable()
    {
        var description = DismErrors.Describe(new COMException("0xC0040009", unchecked((int)0xC0040009)));

        Assert.Equal("DISM error 0xC0040009: DISM could not open its log file. Check the --dism-log path is writable.", description);
    }

    [Fact]
    public void Describe_UnknownCode_KeepsTheMessage()
    {
        var description = DismErrors.Describe(new COMException("Something odd", unchecked((int)0xC0049999)));

        Assert.Equal("DISM error 0xC0049999: Something odd", description);
    }

    [Fact]
    public void Describe_OtherExceptions_AreNotDismErrors()
    {
        Assert.Null(DismErrors.Describe(new InvalidOperationException("nope")));
    }
}
