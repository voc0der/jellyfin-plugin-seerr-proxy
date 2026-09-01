using System.Net;
using Jellyfin.Plugin.SeerrProxy.Security;
using Jellyfin.Plugin.SeerrProxy.Seerr;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class PluginServiceRegistratorTests
{
    private static ServiceProvider Register()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void RegisterServices_ResolvesTheSeerrClient()
    {
        using var provider = Register();

        Assert.IsType<SeerrClient>(provider.GetRequiredService<ISeerrClient>());
    }

    [Fact]
    public void RegisterServices_SharesOneRateLimiterAcrossRequests()
    {
        using var provider = Register();

        // A per-request limiter would bound nothing at all.
        Assert.Same(
            provider.GetRequiredService<SeerrProxyRateLimiter>(),
            provider.GetRequiredService<SeerrProxyRateLimiter>());
    }

    [Fact]
    public void RegisterServices_SharesOneSecretSourceAndOneClient()
    {
        using var provider = Register();

        Assert.Same(
            provider.GetRequiredService<SeerrProxySecretSource>(),
            provider.GetRequiredService<SeerrProxySecretSource>());
        Assert.Same(
            provider.GetRequiredService<ISeerrClient>(),
            provider.GetRequiredService<ISeerrClient>());
    }

    [Fact]
    public void RegisterServices_DoesNotPublishABareHttpClient()
    {
        using var provider = Register();

        // Registering HttpClient into Jellyfin's own container would hand this plugin's
        // handler configuration to unrelated consumers.
        Assert.Null(provider.GetService<HttpClient>());
    }

    /// <summary>
    /// The load-bearing one. .NET strips <c>Authorization</c> across a cross-origin
    /// redirect but carries custom headers, so following a redirect would send the Seerr
    /// API key in <c>X-Api-Key</c> to whatever host the redirect names.
    /// </summary>
    [Fact]
    public void CreateHandler_DoesNotFollowRedirects()
    {
        using var handler = PluginServiceRegistrator.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void CreateHandler_RefreshesPooledConnections()
    {
        using var handler = PluginServiceRegistrator.CreateHandler();

        // Without this a singleton client pins the first DNS answer for the life of the
        // process, so a Seerr container that moves address stays unreachable.
        Assert.True(handler.PooledConnectionLifetime < TimeSpan.FromHours(1));
        Assert.True(handler.PooledConnectionLifetime > TimeSpan.Zero);
    }

    [Fact]
    public void CreateHandler_DecompressesResponses()
    {
        using var handler = PluginServiceRegistrator.CreateHandler();

        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
    }
}
