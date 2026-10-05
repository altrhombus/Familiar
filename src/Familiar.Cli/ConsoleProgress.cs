using Familiar.Core.Servicing;

namespace Familiar.Cli;

/// <summary>Rewrites one status line per operation; log messages go above it.</summary>
internal sealed class ConsoleProgress : IProgress<ServicingProgress>
{
    private readonly Lock _gate = new();
    private bool _onStatusLine;

    public void Report(ServicingProgress value)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        lock (_gate)
        {
            var percent = value.Percent is { } p ? $" {p,5:0.0}%" : string.Empty;
            var text = $"  {value.Operation}{percent}";
            var width = Math.Max(1, Console.WindowWidth - 1);
            Console.Write("\r" + (text.Length > width ? text[..width] : text.PadRight(width)));
            _onStatusLine = true;
        }
    }

    public void Log(string message)
    {
        lock (_gate)
        {
            if (_onStatusLine)
            {
                Console.Write("\r" + new string(' ', Math.Max(1, Console.WindowWidth - 1)) + "\r");
                _onStatusLine = false;
            }

            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
        }
    }
}
