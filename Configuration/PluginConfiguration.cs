using System.Xml.Serialization;
using Jellyfin.Plugin.SeerrProxy.Security;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeerrProxy.Configuration;

/// <summary>
/// Seerr Proxy plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaximumTimeoutSeconds = 300;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Enabled = false;
        SeerrBaseUrl = string.Empty;
        SeerrApiKey = string.Empty;
        RequestTimeoutSeconds = DefaultTimeoutSeconds;
    }

    /// <summary>
    /// Gets or sets a value indicating whether proxy endpoints are enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Seerr base URL.
    /// </summary>
    public string SeerrBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Seerr API key.
    /// </summary>
    /// <remarks>
    /// Only used when the deployment does not supply the key through the environment.
    /// A key stored here is readable by any caller that can reach Jellyfin's plugin
    /// configuration endpoint, which on 10.11.x includes every API key on the server;
    /// see <c>docs/SECURITY.md</c> for the environment-supplied alternative.
    /// </remarks>
    public string SeerrApiKey { get; set; }

    /// <summary>
    /// Gets a value indicating whether the Seerr API key comes from the environment.
    /// </summary>
    /// <remarks>
    /// Purely informational, for the configuration page: when this is <c>true</c> the
    /// API key field is inert, because the environment value takes precedence over
    /// anything stored here. Excluded from XML so it never round-trips to disk.
    /// </remarks>
    [XmlIgnore]
    public bool ApiKeyFromEnvironment => SeerrProxySecretSource.Default.GetEnvironmentApiKey() is not null;

    /// <summary>
    /// Gets or sets the outbound Seerr request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets the configured timeout clamped to a safe range.
    /// </summary>
    /// <returns>The clamped timeout.</returns>
    public TimeSpan GetRequestTimeout()
    {
        var seconds = RequestTimeoutSeconds;
        if (seconds <= 0)
        {
            seconds = DefaultTimeoutSeconds;
        }

        return TimeSpan.FromSeconds(Math.Min(seconds, MaximumTimeoutSeconds));
    }
}
