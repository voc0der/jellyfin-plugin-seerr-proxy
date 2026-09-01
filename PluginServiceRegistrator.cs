using System.Net;
using Jellyfin.Plugin.SeerrProxy.Security;
using Jellyfin.Plugin.SeerrProxy.Seerr;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrProxy;

/// <summary>
/// Registers plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// How long a pooled connection to Seerr may live before it is re-established.
    /// </summary>
    /// <remarks>
    /// A singleton <see cref="HttpClient"/> otherwise pins the first DNS answer for the
    /// life of the process, so a Seerr container that moves to a new address stays
    /// unreachable until Jellyfin restarts.
    /// </remarks>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SeerrProxySecretSource>();
        serviceCollection.AddSingleton<SeerrProxyRateLimiter>();

        // Built here rather than registered as a bare HttpClient: the handler settings
        // below are security-relevant, and registering HttpClient into Jellyfin's own
        // container would hand this configuration to unrelated consumers as well.
        serviceCollection.AddSingleton<ISeerrClient>(serviceProvider => new SeerrClient(
            CreateHttpClient(),
            serviceProvider.GetRequiredService<SeerrProxySecretSource>(),
            serviceProvider.GetRequiredService<ILogger<SeerrClient>>()));
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // The Seerr API key travels in a custom X-Api-Key header, and .NET only
            // strips Authorization across a cross-origin redirect — a custom header is
            // carried to whatever host the redirect names. Following redirects would
            // therefore turn an open redirect in front of Seerr, or a plain-HTTP
            // man-in-the-middle, into a way to exfiltrate the key. Nothing this plugin
            // calls redirects, so refusing them costs nothing.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = ConnectionLifetime
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            // Per-request deadlines come from the configured timeout, applied with a
            // linked CancellationTokenSource in SeerrClient. Leaving the client's own
            // 100-second default in place would silently cap a longer configured value.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
