using Familiar.Core.Updates;

namespace Familiar.Core.Tests.Updates;

public sealed class UpdateSignatureTests : IDisposable
{
    // The runtime's own CoreLib is Authenticode-signed by Microsoft on every machine these tests run on.
    private static readonly string SignedByMicrosoft = typeof(object).Assembly.Location;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "familiar-tests", Guid.NewGuid().ToString("N"));

    public UpdateSignatureTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Check_AcceptsAFileSignedByMicrosoft()
    {
        Assert.Null(UpdateSignature.Check(SignedByMicrosoft));
    }

    [Fact]
    public void Check_RejectsAnUnsignedFile()
    {
        var path = Path.Combine(_folder, "update.msu");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Equal("it has no signature", UpdateSignature.Check(path));
    }

    [Fact]
    public void Check_RejectsASignedFileThatWasChanged()
    {
        var path = Path.Combine(_folder, "changed.dll");
        var bytes = File.ReadAllBytes(SignedByMicrosoft);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.NotNull(UpdateSignature.Check(path));
    }
}
