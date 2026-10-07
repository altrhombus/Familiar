namespace Familiar.Cli;

/// <summary>
/// Ctrl+C handling. The first press cancels the running command and lets it clean up: discarding a mounted image
/// takes minutes, far longer than System.CommandLine's default two-second grace period before it ends the process.
/// A second press exits at once.
/// </summary>
internal sealed class Interruption(TextWriter error) : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private int _presses;

    public CancellationToken Token => _cancellation.Token;

    /// <summary>Handles one Ctrl+C. Returns true to keep the process running while the command cleans up.</summary>
    public bool OnInterrupt()
    {
        if (Interlocked.Increment(ref _presses) > 1)
        {
            error.WriteLine("Exiting without cleaning up. Run 'familiar cleanup' to discard images left mounted.");
            return false;
        }

        error.WriteLine("Cancelling. Mounted images are being discarded, which can take a few minutes; press Ctrl+C again to exit now.");
        _cancellation.Cancel();
        return true;
    }

    public void Dispose() => _cancellation.Dispose();
}
