using Familiar.Core.ConfigMgr;
using Familiar.Core.Recipes;

namespace Familiar.Core.Tests.ConfigMgr;

public sealed class ConfigMgrErrorsTests
{
    private static readonly ConfigMgrSection Section = new() { SiteServer = "cm01.contoso.com", PackageId = "PS100123" };

    [Fact]
    public async Task Publish_TransportFailure_SaysTheImageIsBuiltAndHowToRetry()
    {
        var ex = await Assert.ThrowsAsync<BuildException>(() => Publish(new HttpRequestException("No such host is known."), TestContext.Current.CancellationToken));

        Assert.Contains("No such host is known.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("familiar configmgr publish", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_Timeout_SaysTheSiteMayStillBeWorking()
    {
        var ex = await Assert.ThrowsAsync<BuildException>(() => Publish(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."), TestContext.Current.CancellationToken));

        Assert.Contains("did not answer in time", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_CancelledByTheUser_IsNotReportedAsAFailure()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => Publish(new TaskCanceledException(), cancelled.Token));
    }

    [Fact]
    public async Task Publish_Bug_IsNotHidden()
    {
        await Assert.ThrowsAsync<NullReferenceException>(() => Publish(new NullReferenceException(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Describe_NamesAnUntrustedCertificate()
    {
        var ex = new HttpRequestException("The SSL connection could not be established.", new System.Security.Authentication.AuthenticationException());

        Assert.Contains("certificate is not trusted", ConfigMgrErrors.Describe(ex), StringComparison.Ordinal);
    }

    private static Task<IReadOnlyList<string>> Publish(Exception connectFailure, CancellationToken cancellationToken) =>
        BuildRunner.PublishAsync(
            RecipeSerializer.Template() with { ConfigMgr = Section },
            Section,
            @"D:\Familiar\Output\install.wim",
            facts: null,
            connect: (_, _, _) => Task.FromException<IConfigMgrConnection>(connectFailure),
            progress: null,
            log: _ => { },
            cancellationToken);
}
