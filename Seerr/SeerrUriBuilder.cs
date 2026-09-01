using Jellyfin.Plugin.SeerrProxy.Configuration;

namespace Jellyfin.Plugin.SeerrProxy.Seerr;

/// <summary>
/// Composes absolute Seerr API URLs from the configured base URL.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="SeerrClient"/> because the containment check below is
/// the last thing standing between a caller-supplied path and the rest of the Seerr host,
/// and it deserves to be readable and directly testable.
/// </remarks>
public static class SeerrUriBuilder
{
    /// <summary>
    /// Builds an absolute Seerr API URL for a relative path.
    /// </summary>
    /// <param name="configuration">Plugin configuration supplying the base URL.</param>
    /// <param name="relativePath">Path under <c>/api/v1</c>, optionally with a query string.</param>
    /// <returns>The absolute URL to request.</returns>
    /// <exception cref="SeerrConfigurationException">
    /// The base URL is missing or unusable, or the composed URL would fall outside the
    /// Seerr API root.
    /// </exception>
    public static Uri Build(PluginConfiguration configuration, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(relativePath);

        var apiRoot = BuildApiRoot(configuration);
        var resolved = new Uri(apiRoot, relativePath.TrimStart('/'));

        // Belt and braces on top of Security.ApiAllowlist: whatever the relative path
        // contained, the result must still sit under the Seerr /api/v1 root computed
        // above. Uri resolution honours dot segments, so this is the check that makes
        // escaping the prefix impossible rather than merely difficult.
        if (!apiRoot.IsBaseOf(resolved))
        {
            throw new SeerrConfigurationException("Refusing to build a Seerr URL outside the configured API root.");
        }

        return resolved;
    }

    /// <summary>
    /// Resolves the configured base URL to the Seerr <c>/api/v1/</c> root.
    /// </summary>
    /// <param name="configuration">Plugin configuration supplying the base URL.</param>
    /// <returns>The API root, always with a trailing slash.</returns>
    /// <exception cref="SeerrConfigurationException">The base URL is missing or unusable.</exception>
    public static Uri BuildApiRoot(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration.SeerrBaseUrl))
        {
            throw new SeerrConfigurationException("Seerr base URL is not configured.");
        }

        var trimmedBaseUrl = configuration.SeerrBaseUrl.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(trimmedBaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SeerrConfigurationException("Seerr base URL must be an absolute HTTP or HTTPS URL.");
        }

        // A base URL carrying credentials would put them in every outbound request and in
        // any log line naming the URL. Refuse rather than quietly strip them.
        if (!string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new SeerrConfigurationException("Seerr base URL must not contain embedded credentials.");
        }

        var builder = new UriBuilder(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        var basePath = builder.Path.TrimEnd('/');
        if (!basePath.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            && !basePath.Equals("api/v1", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = string.IsNullOrWhiteSpace(basePath) || basePath == "/"
                ? "api/v1/"
                : basePath.TrimStart('/') + "/api/v1/";
        }
        else
        {
            builder.Path = basePath.TrimStart('/') + "/";
        }

        return builder.Uri;
    }
}
