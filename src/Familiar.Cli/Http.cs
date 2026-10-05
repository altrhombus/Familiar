namespace Familiar.Cli;

internal static class Http
{
    public static HttpClient Create()
    {
        // Downloads are multi-gigabyte; per-request timeouts are handled by cancellation instead.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Familiar/" + typeof(Http).Assembly.GetName().Version?.ToString(3));
        return http;
    }
}
