using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SeerrProxy.Configuration;
using Jellyfin.Plugin.SeerrProxy.Models;
using Jellyfin.Plugin.SeerrProxy.Security;
using Jellyfin.Plugin.SeerrProxy.Seerr;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SeerrProxyPlugin = Jellyfin.Plugin.SeerrProxy.Plugin;

namespace Jellyfin.Plugin.SeerrProxy.Api;

/// <summary>
/// Authenticated Jellyfin-to-Seerr proxy endpoints.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/SeerrProxy")]
[Produces(MediaTypeNames.Application.Json)]
public class SeerrProxyController : ControllerBase
{
    /// <summary>
    /// Most bytes this plugin will accept in a proxied request body.
    /// </summary>
    /// <remarks>
    /// A Seerr request payload is a few hundred bytes. The body is buffered whole before
    /// it is parsed, so the bound keeps a client from spending the Jellyfin server's
    /// memory on a body that Seerr would reject anyway.
    /// </remarks>
    private const int MaxRequestBodyBytes = 256 * 1024;

    private const int ReadBufferSize = 8192;

    private readonly ISeerrClient _seerrClient;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IPluginManager _pluginManager;
    private readonly SeerrProxySecretSource _secretSource;
    private readonly SeerrProxyRateLimiter _rateLimiter;
    private readonly ILogger<SeerrProxyController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrProxyController"/> class.
    /// </summary>
    /// <param name="seerrClient">Seerr API client.</param>
    /// <param name="authorizationContext">Jellyfin authorization context.</param>
    /// <param name="pluginManager">Jellyfin plugin manager, used for the kill switch.</param>
    /// <param name="secretSource">Supplies the operator secret hash and the Seerr API key.</param>
    /// <param name="rateLimiter">Bounds how often this plugin does work.</param>
    /// <param name="logger">Logger.</param>
    public SeerrProxyController(
        ISeerrClient seerrClient,
        IAuthorizationContext authorizationContext,
        IPluginManager pluginManager,
        SeerrProxySecretSource secretSource,
        SeerrProxyRateLimiter rateLimiter,
        ILogger<SeerrProxyController> logger)
    {
        _seerrClient = seerrClient;
        _authorizationContext = authorizationContext;
        _pluginManager = pluginManager;
        _secretSource = secretSource;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    /// <summary>
    /// Gets plugin status for the current Jellyfin user.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Status returned.</response>
    /// <response code="401">Caller is authenticated but is not a Jellyfin user.</response>
    /// <response code="404">The plugin is not active.</response>
    /// <response code="429">Too many requests; retry after the indicated delay.</response>
    /// <returns>Status response.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(typeof(SeerrProxyStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SeerrProxyStatusResponse>> GetStatus(CancellationToken cancellationToken)
    {
        if (!IsPluginActive())
        {
            return PluginInactive();
        }

        var configuration = GetConfiguration();
        var jellyfinUser = await GetAuthenticatedJellyfinUser().ConfigureAwait(false);
        if (jellyfinUser is null)
        {
            return Unauthorized(Error(StatusCodes.Status401Unauthorized, "MissingJellyfinUser", "Authenticated request is not associated with a Jellyfin user."));
        }

        var throttled = EnforceProxyRateLimit(jellyfinUser.UserId);
        if (throttled is not null)
        {
            return throttled;
        }

        var configured = _secretSource.IsConfigured(configuration);
        var response = new SeerrProxyStatusResponse
        {
            Enabled = configuration.Enabled,
            Configured = configured,
            JellyfinUserId = jellyfinUser.UserId
        };

        if (!configuration.Enabled || !configured)
        {
            return Ok(response);
        }

        try
        {
            var seerrUser = await _seerrClient.GetUserByJellyfinIdAsync(configuration, jellyfinUser.UserId, cancellationToken)
                .ConfigureAwait(false);
            response.Linked = true;
            response.SeerrReachable = true;
            response.SeerrUserId = seerrUser.Id;
            response.DisplayName = seerrUser.GetSafeDisplayName();
        }
        catch (SeerrApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            response.Linked = false;
            response.SeerrReachable = true;
        }
        catch (SeerrConnectionException ex)
        {
            _logger.LogWarning(ex, "Unable to reach Seerr while checking status for Jellyfin user {JellyfinUserId}", jellyfinUser.UserId);
            response.SeerrReachable = false;
            response.MappingError = "Seerr is unreachable.";
        }
        catch (SeerrConfigurationException ex)
        {
            response.MappingError = ex.Message;
        }

        return Ok(response);
    }

    /// <summary>
    /// Forwards allowlisted Seerr API requests as the current Jellyfin user's linked Seerr user.
    /// </summary>
    /// <param name="path">Seerr API path under /api/v1.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="401">Caller is authenticated but is not a Jellyfin user.</response>
    /// <response code="404">The plugin is not active, or this endpoint is not allowlisted.</response>
    /// <response code="413">The request body is larger than this plugin will forward.</response>
    /// <response code="429">Too many requests; retry after the indicated delay.</response>
    /// <returns>Seerr API response.</returns>
    [AcceptVerbs("GET", "POST", "PUT", "DELETE")]
    [Route("api/v1/{**path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ForwardApiRequest([FromRoute] string? path, CancellationToken cancellationToken)
    {
        if (!IsPluginActive())
        {
            return PluginInactive();
        }

        // The allowlist runs before anything else so an unsupported path costs no work
        // and discloses nothing about how the plugin is configured.
        var normalizedPath = ApiAllowlist.NormalizePath(path);
        var query = Request.QueryString.Value;
        if (!ApiAllowlist.IsAllowed(Request.Method, normalizedPath) || !ApiAllowlist.IsAllowedQuery(query))
        {
            return NotFound(Error(StatusCodes.Status404NotFound, "UnsupportedProxyEndpoint", "This Seerr API endpoint is not available through the Jellyfin proxy."));
        }

        var configuration = GetConfiguration();
        var disabledOrUnconfigured = EnsureEnabledAndConfigured(configuration);
        if (disabledOrUnconfigured is not null)
        {
            return disabledOrUnconfigured;
        }

        var jellyfinUser = await GetAuthenticatedJellyfinUser().ConfigureAwait(false);
        if (jellyfinUser is null)
        {
            return Unauthorized(Error(StatusCodes.Status401Unauthorized, "MissingJellyfinUser", "Authenticated request is not associated with a Jellyfin user."));
        }

        // Rate limit before any outbound call, so this endpoint cannot be turned into an
        // amplifier pointed at Seerr.
        var throttled = EnforceProxyRateLimit(jellyfinUser.UserId);
        if (throttled is not null)
        {
            return throttled;
        }

        SeerrUser seerrUser;
        try
        {
            seerrUser = await _seerrClient.GetUserByJellyfinIdAsync(configuration, jellyfinUser.UserId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (HandleProxyException(ex, out var resolveError))
        {
            return resolveError;
        }

        JsonNode? payload = null;
        if (HttpMethods.IsPost(Request.Method) || HttpMethods.IsPut(Request.Method))
        {
            var body = await ReadJsonBody(cancellationToken).ConfigureAwait(false);
            if (body.TooLarge)
            {
                return StatusCode(
                    StatusCodes.Status413PayloadTooLarge,
                    Error(StatusCodes.Status413PayloadTooLarge, "RequestBodyTooLarge", "Request body is larger than this proxy will forward."));
            }

            if (body.Invalid)
            {
                return BadRequest(Error(StatusCodes.Status400BadRequest, "InvalidJson", "Request body must be valid JSON."));
            }

            payload = ForwardedPayload.StripIdentity(body.Payload, out var removedProperties);
            if (removedProperties.Count > 0)
            {
                // Not an error for the caller — the request proceeds as themselves — but
                // an attempt to act as another Seerr user is worth an audit line.
                _logger.LogWarning(
                    "Removed client-supplied identity fields {Fields} from a proxied request for Jellyfin user {JellyfinUserId}",
                    LogSanitizer.ForLog(string.Join(", ", removedProperties)),
                    jellyfinUser.UserId);
            }
        }

        var relativePath = normalizedPath + query;
        try
        {
            var result = await _seerrClient.ForwardApiRequestAsync(
                    configuration,
                    seerrUser.Id,
                    new HttpMethod(Request.Method),
                    relativePath,
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);

            return result.Body is null
                ? StatusCode(result.StatusCode)
                : StatusCode(result.StatusCode, result.Body);
        }
        catch (Exception ex) when (HandleProxyException(ex, out var actionResult, notFoundMeansUnlinked: false))
        {
            return actionResult;
        }
    }

    /// <summary>
    /// Tests Seerr reachability and the configured API key.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Seerr was reachable and the API key was accepted.</response>
    /// <response code="403">Caller is not elevated, or the operator secret is missing or wrong.</response>
    /// <response code="404">The plugin is not active.</response>
    /// <response code="429">Too many requests; retry after the indicated delay.</response>
    /// <response code="503">The plugin is not configured.</response>
    /// <returns>Connection test response.</returns>
    [HttpPost("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(typeof(TestConnectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TestConnectionResponse>> TestConnection(CancellationToken cancellationToken)
    {
        if (!IsPluginActive())
        {
            return PluginInactive();
        }

        // Before the secret is examined, so that a caller holding any Jellyfin API key —
        // which 10.11.x treats as an administrator — cannot guess it at speed.
        if (!_rateLimiter.TryAcquireAdmin(out var retryAfter))
        {
            _logger.LogWarning("Seerr Proxy connection test rejected: rate limit exceeded");
            return TooManyRequests(retryAfter);
        }

        // Gate two. Jellyfin's elevation policy is gate one, applied by [Authorize] above.
        if (!IsOperatorSecretValid())
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                Error(StatusCodes.Status403Forbidden, "OperatorSecretRequired", "This endpoint requires the operator secret."));
        }

        var configuration = GetConfiguration();
        if (!_secretSource.IsConfigured(configuration))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(StatusCodes.Status503ServiceUnavailable, "PluginNotConfigured", "Seerr base URL and API key must be configured first."));
        }

        try
        {
            var status = await _seerrClient.GetStatusAsync(configuration, cancellationToken).ConfigureAwait(false);
            await _seerrClient.ValidateApiKeyAsync(configuration, cancellationToken).ConfigureAwait(false);

            return Ok(new TestConnectionResponse
            {
                Reachable = true,
                Authenticated = true,
                Version = status.Version,
                ApiKeySource = _secretSource.GetEnvironmentApiKey() is not null ? "environment" : "configuration",
                Message = "Successfully connected to Seerr."
            });
        }
        catch (Exception ex) when (HandleProxyException(ex, out var actionResult, notFoundMeansUnlinked: false))
        {
            return actionResult;
        }
    }

    private static PluginConfiguration GetConfiguration()
    {
        return SeerrProxyPlugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    private static ErrorResponse Error(int statusCode, string error, string message)
    {
        return new ErrorResponse(statusCode, error, message);
    }

    private static int ToClientStatusCode(HttpStatusCode upstreamStatusCode)
    {
        var statusCode = (int)upstreamStatusCode;
        return statusCode is >= 400 and < 500 ? statusCode : StatusCodes.Status502BadGateway;
    }

    /// <summary>
    /// Determines whether Jellyfin currently considers this plugin fully active.
    /// </summary>
    /// <remarks>
    /// Jellyfin registers plugin controllers from loaded assemblies once at startup,
    /// independently of plugin state, so a plugin disabled at runtime keeps serving its
    /// routes until the server restarts. Requires <see cref="PluginStatus.Active"/>
    /// exactly rather than <c>LocalPlugin.IsEnabledAndSupported</c>: disabling a running
    /// plugin writes <c>Disabled</c> to its manifest but leaves the in-memory status at
    /// <see cref="PluginStatus.Restart"/>, which <c>IsEnabledAndSupported</c> still counts
    /// as enabled.
    /// <para>
    /// Fails closed: if no plugin record matches this assembly's ID, the plugin is in an
    /// unexpected state and the endpoints refuse to serve.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> only if the plugin record is present, supported, and active.</returns>
    private bool IsPluginActive()
    {
        var plugin = _pluginManager.Plugins
            .FirstOrDefault(candidate => candidate.Id.Equals(SeerrProxyPlugin.PluginId));

        return plugin is not null
            && plugin.IsEnabledAndSupported
            && plugin.Manifest.Status == PluginStatus.Active;
    }

    /// <summary>
    /// Verifies the operator secret presented on this request.
    /// </summary>
    /// <remarks>
    /// Re-reads the configured hash on every call, so removing or rotating it takes
    /// effect immediately. When no hash is configured the elevated endpoints fall back to
    /// Jellyfin elevation alone, unless the deployment has set
    /// <see cref="SeerrProxySecretSource.RequireAdminSecretVariable"/> — which makes the
    /// gate mandatory, so a hash file that disappears cannot silently downgrade it.
    /// </remarks>
    /// <returns><c>true</c> if the request may proceed past the operator gate.</returns>
    private bool IsOperatorSecretValid()
    {
        var configuredHash = _secretSource.GetAdminSecretHash();
        if (!AdminSecretVerifier.IsConfigured(configuredHash))
        {
            if (_secretSource.IsAdminSecretRequired())
            {
                _logger.LogWarning(
                    "Seerr Proxy elevated request rejected: {Variable} is set but no usable secret hash is configured",
                    SeerrProxySecretSource.RequireAdminSecretVariable);
                return false;
            }

            return true;
        }

        Request.Headers.TryGetValue(AdminSecretVerifier.HeaderName, out var presentedSecret);
        if (!AdminSecretVerifier.Verify(configuredHash, presentedSecret))
        {
            _logger.LogWarning("Seerr Proxy elevated request rejected: invalid or missing operator secret");
            return false;
        }

        return true;
    }

    private ObjectResult PluginInactive()
    {
        _logger.LogWarning("Seerr Proxy request rejected: the plugin is not active");
        return NotFound(Error(StatusCodes.Status404NotFound, "PluginInactive", "Seerr Proxy is not active."));
    }

    private ObjectResult TooManyRequests(TimeSpan retryAfter)
    {
        Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);

        return StatusCode(
            StatusCodes.Status429TooManyRequests,
            Error(StatusCodes.Status429TooManyRequests, "RateLimited", "Too many Seerr Proxy requests. Retry shortly."));
    }

    private ObjectResult? EnforceProxyRateLimit(string jellyfinUserId)
    {
        if (_rateLimiter.TryAcquireProxy(jellyfinUserId, out var retryAfter))
        {
            return null;
        }

        _logger.LogWarning(
            "Seerr Proxy request rejected for Jellyfin user {JellyfinUserId}: rate limit exceeded",
            jellyfinUserId);
        return TooManyRequests(retryAfter);
    }

    private ObjectResult? EnsureEnabledAndConfigured(PluginConfiguration configuration)
    {
        if (!configuration.Enabled)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                Error(StatusCodes.Status403Forbidden, "PluginDisabled", "Seerr Proxy is disabled."));
        }

        if (!_secretSource.IsConfigured(configuration))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(StatusCodes.Status503ServiceUnavailable, "PluginNotConfigured", "Seerr base URL and API key must be configured first."));
        }

        return null;
    }

    private async Task<AuthenticatedJellyfinUser?> GetAuthenticatedJellyfinUser()
    {
        var authorizationInfo = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);

        // An API key authenticates without being a user. Jellyfin 10.11.x grants such a
        // caller the Administrator role, so refusing here is what keeps an API key from
        // proxying as somebody: there is no user to resolve to a Seerr account.
        if (authorizationInfo.UserId.Equals(Guid.Empty))
        {
            return null;
        }

        return new AuthenticatedJellyfinUser(
            authorizationInfo.UserId.ToString("N", CultureInfo.InvariantCulture),
            authorizationInfo.User?.Username);
    }

    private async Task<BodyReadResult> ReadJsonBody(CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaxRequestBodyBytes)
        {
            return BodyReadResult.Oversized;
        }

        using var accumulator = new MemoryStream();
        var buffer = new byte[ReadBufferSize];

        int read;
        while ((read = await Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (accumulator.Length + read > MaxRequestBodyBytes)
            {
                return BodyReadResult.Oversized;
            }

            accumulator.Write(buffer, 0, read);
        }

        var body = Encoding.UTF8.GetString(accumulator.GetBuffer(), 0, (int)accumulator.Length);
        if (string.IsNullOrWhiteSpace(body))
        {
            return BodyReadResult.Empty;
        }

        try
        {
            return new BodyReadResult(JsonNode.Parse(body), false, false);
        }
        catch (JsonException)
        {
            return BodyReadResult.Malformed;
        }
    }

    private bool HandleProxyException(Exception exception, out ObjectResult actionResult, bool notFoundMeansUnlinked = true)
    {
        switch (exception)
        {
            case SeerrApiException seerrApiException when notFoundMeansUnlinked && seerrApiException.StatusCode == HttpStatusCode.NotFound:
                actionResult = StatusCode(
                    StatusCodes.Status404NotFound,
                    Error(StatusCodes.Status404NotFound, "SeerrUserNotLinked", "The authenticated Jellyfin user is not linked or imported in Seerr."));
                return true;
            case SeerrApiException seerrApiException:
                actionResult = StatusCode(
                    ToClientStatusCode(seerrApiException.StatusCode),
                    Error(ToClientStatusCode(seerrApiException.StatusCode), "SeerrError", seerrApiException.Message));
                return true;
            case SeerrConnectionException seerrConnectionException:
                _logger.LogWarning(seerrConnectionException, "Unable to reach Seerr");
                actionResult = StatusCode(
                    StatusCodes.Status502BadGateway,
                    Error(StatusCodes.Status502BadGateway, "SeerrUnreachable", "Seerr is unreachable."));
                return true;
            case SeerrConfigurationException seerrConfigurationException:
                actionResult = StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    Error(StatusCodes.Status503ServiceUnavailable, "PluginNotConfigured", seerrConfigurationException.Message));
                return true;
            default:
                actionResult = StatusCode(
                    StatusCodes.Status500InternalServerError,
                    Error(StatusCodes.Status500InternalServerError, "UnexpectedError", "Unexpected proxy error."));
                return false;
        }
    }

    private sealed record AuthenticatedJellyfinUser(string UserId, string? DisplayName);

    private sealed record BodyReadResult(JsonNode? Payload, bool TooLarge, bool Invalid)
    {
        public static BodyReadResult Empty { get; } = new(null, false, false);

        public static BodyReadResult Oversized { get; } = new(null, true, false);

        public static BodyReadResult Malformed { get; } = new(null, false, true);
    }
}
