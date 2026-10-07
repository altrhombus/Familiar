namespace Familiar.Cli.Tests;

public sealed class InterruptionTests
{
    [Fact]
    public void FirstPress_CancelsAndKeepsTheProcessRunning()
    {
        using var error = new StringWriter();
        using var interruption = new Interruption(error);

        Assert.True(interruption.OnInterrupt());
        Assert.True(interruption.Token.IsCancellationRequested);
        Assert.Contains("Ctrl+C again", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SecondPress_LetsTheProcessExit()
    {
        using var error = new StringWriter();
        using var interruption = new Interruption(error);
        interruption.OnInterrupt();

        Assert.False(interruption.OnInterrupt());
        Assert.Contains("familiar cleanup", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeAnyPress_NothingIsCancelled()
    {
        using var interruption = new Interruption(TextWriter.Null);

        Assert.False(interruption.Token.IsCancellationRequested);
    }
}
