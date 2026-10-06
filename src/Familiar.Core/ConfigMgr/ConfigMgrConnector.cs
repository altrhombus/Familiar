namespace Familiar.Core.ConfigMgr;

public static class ConfigMgrConnector
{
    /// <summary>
    /// Connects with the requested transport. <see cref="ConfigMgrTransport.Auto"/> tries the administration
    /// service first (it needs only HTTPS) and falls back to WMI, logging why.
    /// </summary>
    public static async Task<IConfigMgrConnection> ConnectAsync(
        string siteServer, string? siteCode, ConfigMgrTransport transport, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteServer);
        log ??= _ => { };

        if (transport is ConfigMgrTransport.Auto or ConfigMgrTransport.AdminService)
        {
            try
            {
                var adminService = await AdminServiceConnection.ConnectAsync(AdminServiceConnection.CreateClient(siteServer), siteCode, ownsClient: true, cancellationToken).ConfigureAwait(false);
                return transport == ConfigMgrTransport.AdminService
                    ? adminService
                    : new FallbackConnection(adminService, async ct => await WmiConnection.ConnectAsync(siteServer, adminService.SiteCode, ct).ConfigureAwait(false), log);
            }
            catch (Exception ex) when (ex is HttpRequestException or ConfigMgrException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (transport == ConfigMgrTransport.AdminService)
                {
                    throw ex as ConfigMgrException ?? new ConfigMgrException($"The administration service on {siteServer} did not answer: {Describe(ex)}", ex);
                }

                log($"Administration service on {siteServer} unavailable ({Describe(ex)}); using WMI.");
            }
        }

        return await WmiConnection.ConnectAsync(siteServer, siteCode, cancellationToken).ConfigureAwait(false);
    }

    // Certificate failures are the usual AdminService problem; say so instead of "The SSL connection could not be established".
    private static string Describe(Exception ex) =>
        ex.InnerException is System.Security.Authentication.AuthenticationException
            ? "its HTTPS certificate is not trusted by this machine"
            : ex.Message;
}
