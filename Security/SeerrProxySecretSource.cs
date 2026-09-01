using Jellyfin.Plugin.SeerrProxy.Configuration;

namespace Jellyfin.Plugin.SeerrProxy.Security;

/// <summary>
/// Supplies this plugin's secrets from the deployment environment.
/// </summary>
/// <remarks>
/// Two distinct secrets come from here:
/// <list type="bullet">
/// <item>
/// the <em>operator secret hash</em>, which gates the elevated endpoints and is never
/// stored by the plugin at all;
/// </item>
/// <item>
/// the <em>Seerr API key</em>, which the plugin must hold in plaintext to talk to
/// Seerr, but which an operator may supply here so it never enters Jellyfin's plugin
/// configuration and therefore can never be read back through
/// <c>GET /Plugins/Configuration/{guid}</c>.
/// </item>
/// </list>
/// <para>
/// Each value may be given directly or as a path to a file holding it. The file form
/// takes precedence and is the preferred one: the file can be root-owned with
/// restrictive permissions, and only its <em>path</em> ever reaches a log.
/// </para>
/// <para>
/// Values are read on each call, so rotating a file or restarting with a new
/// environment value takes effect without any plugin action.
/// </para>
/// </remarks>
public sealed class SeerrProxySecretSource
{
    /// <summary>
    /// Environment variable holding the hex-encoded SHA-256 hash of the operator secret.
    /// </summary>
    /// <remarks>
    /// Deliberately not prefixed <c>JELLYFIN_</c>: Jellyfin logs every environment
    /// variable starting with JELLYFIN_, DOTNET_, or ASPNETCORE_ at startup
    /// (<c>StartupHelpers.LogEnvironmentInfo</c>), which would print this value into the
    /// server log on every boot.
    /// </remarks>
    public const string AdminSecretHashVariable = "SEERR_PROXY_ADMIN_SECRET_HASH";

    /// <summary>
    /// Environment variable holding a path to a file containing that hash.
    /// </summary>
    public const string AdminSecretHashFileVariable = "SEERR_PROXY_ADMIN_SECRET_HASH_FILE";

    /// <summary>
    /// Environment variable that makes the operator secret mandatory.
    /// </summary>
    /// <remarks>
    /// Set to <c>1</c>, <c>true</c>, or <c>yes</c> to require the secret unconditionally.
    /// With it set, a missing or malformed hash disables the elevated endpoints entirely
    /// rather than falling back to elevation alone, so a hash file that disappears
    /// cannot silently downgrade the gate. See <c>docs/SECURITY.md</c>.
    /// </remarks>
    public const string RequireAdminSecretVariable = "SEERR_PROXY_REQUIRE_ADMIN_SECRET";

    /// <summary>
    /// Environment variable holding the Seerr API key.
    /// </summary>
    public const string ApiKeyVariable = "SEERR_PROXY_API_KEY";

    /// <summary>
    /// Environment variable holding a path to a file containing the Seerr API key.
    /// </summary>
    public const string ApiKeyFileVariable = "SEERR_PROXY_API_KEY_FILE";

    private static readonly string[] TruthyValues = ["1", "true", "yes", "on"];

    /// <summary>
    /// Gets a process-wide instance for callers that cannot take one by injection.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Configuration.PluginConfiguration"/> needs this: Jellyfin
    /// constructs plugin configuration objects itself, outside the container. Everything
    /// else receives the DI-registered singleton. Both are stateless — the environment is
    /// re-read on every call — so having two instances changes no behaviour.
    /// </remarks>
    public static SeerrProxySecretSource Default { get; } = new();

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, string> _readAllText;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrProxySecretSource"/> class.
    /// </summary>
    public SeerrProxySecretSource()
        : this(Environment.GetEnvironmentVariable, File.ReadAllText)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrProxySecretSource"/> class with
    /// explicit environment and file accessors, for testing.
    /// </summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <param name="readAllText">Reads the full contents of a file.</param>
    public SeerrProxySecretSource(Func<string, string?> getEnvironmentVariable, Func<string, string> readAllText)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _readAllText = readAllText;
    }

    /// <summary>
    /// Gets the configured operator secret hash, or <c>null</c> when none is usable.
    /// </summary>
    /// <remarks>
    /// Fails closed: an unreadable file, an unset variable, or a blank value all yield
    /// <c>null</c>. Callers must not distinguish these cases in responses.
    /// </remarks>
    /// <returns>The hex-encoded hash, or <c>null</c>.</returns>
    public string? GetAdminSecretHash()
    {
        return Read(AdminSecretHashFileVariable, AdminSecretHashVariable);
    }

    /// <summary>
    /// Gets a value indicating whether the operator secret is mandatory.
    /// </summary>
    /// <returns><c>true</c> when the deployment requires the secret unconditionally.</returns>
    public bool IsAdminSecretRequired()
    {
        var value = _getEnvironmentVariable(RequireAdminSecretVariable)?.Trim();
        return !string.IsNullOrEmpty(value)
            && TruthyValues.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the Seerr API key supplied by the environment, or <c>null</c> when none is.
    /// </summary>
    /// <returns>The API key, or <c>null</c>.</returns>
    public string? GetEnvironmentApiKey()
    {
        return Read(ApiKeyFileVariable, ApiKeyVariable);
    }

    /// <summary>
    /// Resolves the Seerr API key the plugin should present, preferring the environment.
    /// </summary>
    /// <param name="configuration">The plugin configuration to fall back to.</param>
    /// <returns>The API key, or <c>null</c> when neither source supplies one.</returns>
    public string? ResolveApiKey(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var fromEnvironment = GetEnvironmentApiKey();
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var fromConfiguration = configuration.SeerrApiKey?.Trim();
        return string.IsNullOrEmpty(fromConfiguration) ? null : fromConfiguration;
    }

    /// <summary>
    /// Gets a value indicating whether the plugin has everything it needs to reach Seerr.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns><c>true</c> when a base URL and an API key are both available.</returns>
    public bool IsConfigured(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return !string.IsNullOrWhiteSpace(configuration.SeerrBaseUrl)
            && ResolveApiKey(configuration) is not null;
    }

    private string? Read(string fileVariable, string valueVariable)
    {
        var path = _getEnvironmentVariable(fileVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var fromFile = _readAllText(path).Trim();
                return string.IsNullOrEmpty(fromFile) ? null : fromFile;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        var fromEnvironment = _getEnvironmentVariable(valueVariable)?.Trim();
        return string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
    }
}
