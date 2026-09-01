using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SeerrProxy.Configuration;
using Jellyfin.Plugin.SeerrProxy.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrProxy.Seerr;

/// <inheritdoc />
public sealed class SeerrClient : ISeerrClient
{
    /// <summary>
    /// Most bytes this plugin will read from a single Seerr response.
    /// </summary>
    /// <remarks>
    /// The body is buffered into a string before it is parsed, so without a bound a
    /// hostile or malfunctioning upstream could exhaust the Jellyfin server's memory one
    /// proxied request at a time. Seerr's largest legitimate responses — a full discover
    /// page, a long request list — are orders of magnitude under this.
    /// </remarks>
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    private const int ReadBufferSize = 8192;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly SeerrProxySecretSource _secretSource;
    private readonly ILogger<SeerrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client supplied by dependency injection.</param>
    /// <param name="secretSource">Supplies the Seerr API key.</param>
    /// <param name="logger">Logger.</param>
    public SeerrClient(HttpClient httpClient, SeerrProxySecretSource secretSource, ILogger<SeerrClient> logger)
    {
        _httpClient = httpClient;
        _secretSource = secretSource;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SeerrStatus> GetStatusAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SeerrUriBuilder.Build(configuration, "status"));
        var result = await SendAsync(configuration, request, cancellationToken).ConfigureAwait(false);

        return Deserialize<SeerrStatus>(result.BodyText) ?? new SeerrStatus();
    }

    /// <inheritdoc />
    public async Task ValidateApiKeyAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SeerrUriBuilder.Build(configuration, "auth/me"));
        AddApiKey(request, configuration);

        await SendAsync(configuration, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SeerrUser> GetUserByJellyfinIdAsync(
        PluginConfiguration configuration,
        string jellyfinUserId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            SeerrUriBuilder.Build(configuration, "user/jellyfin/" + Uri.EscapeDataString(jellyfinUserId)));
        AddApiKey(request, configuration);

        var result = await SendAsync(configuration, request, cancellationToken).ConfigureAwait(false);
        var user = Deserialize<SeerrUser>(result.BodyText);

        if (user is null || user.Id <= 0)
        {
            throw new SeerrApiException(HttpStatusCode.BadGateway, "Seerr returned an invalid user response.");
        }

        return user;
    }

    /// <inheritdoc />
    public async Task<SeerrApiResult> ForwardApiRequestAsync(
        PluginConfiguration configuration,
        int seerrUserId,
        HttpMethod method,
        string relativePath,
        JsonNode? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, SeerrUriBuilder.Build(configuration, relativePath));
        if (payload is not null)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        AddApiKey(request, configuration);

        // The acting identity is an integer resolved server-side from Jellyfin
        // authentication. It is never taken from the request.
        request.Headers.TryAddWithoutValidation("X-API-User", seerrUserId.ToString(CultureInfo.InvariantCulture));

        var result = await SendAsync(configuration, request, cancellationToken).ConfigureAwait(false);
        return new SeerrApiResult(result.StatusCode, ParseJson(result.BodyText));
    }

    private async Task<SeerrTransportResult> SendAsync(
        PluginConfiguration configuration,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutTokenSource.CancelAfter(configuration.GetRequestTimeout());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutTokenSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SeerrConnectionException("Seerr request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SeerrConnectionException("Seerr is unreachable.", ex);
        }

        using (response)
        {
            var body = await ReadBoundedBodyAsync(response, timeoutTokenSource.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new SeerrTransportResult((int)response.StatusCode, body);
            }

            _logger.LogWarning(
                "Seerr API returned HTTP {StatusCode} for {Method} {Path}",
                (int)response.StatusCode,
                request.Method,
                LogSanitizer.ForLog(request.RequestUri?.AbsolutePath));

            // Redirects are not followed (see PluginServiceRegistrator), so one arriving
            // here is a misconfigured base URL rather than a Seerr error. Say so, instead
            // of reporting an opaque 3xx.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new SeerrConfigurationException(
                    "Seerr returned a redirect. Check that the configured base URL uses the correct scheme and path.");
            }

            var message = ExtractErrorMessage(body)
                          ?? string.Create(
                              CultureInfo.InvariantCulture,
                              $"Seerr returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

            throw new SeerrApiException(
                response.StatusCode,
                Sanitize(message, _secretSource.ResolveApiKey(configuration)));
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new SeerrApiException(HttpStatusCode.BadGateway, "Seerr returned a response too large to proxy.");
        }

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (responseStream.ConfigureAwait(false))
        {
            using var accumulator = new MemoryStream();
            var buffer = new byte[ReadBufferSize];

            int read;
            while ((read = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (accumulator.Length + read > MaxResponseBytes)
                {
                    throw new SeerrApiException(HttpStatusCode.BadGateway, "Seerr returned a response too large to proxy.");
                }

                accumulator.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(accumulator.GetBuffer(), 0, (int)accumulator.Length);
        }
    }

    private void AddApiKey(HttpRequestMessage request, PluginConfiguration configuration)
    {
        var apiKey = _secretSource.ResolveApiKey(configuration)
                     ?? throw new SeerrConfigurationException("Seerr API key is not configured.");

        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
    }

    private static T? Deserialize<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static JsonNode? ParseJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // Every Seerr /api/v1 endpoint answers with JSON. Anything else on a success
            // status means the request did not reach Seerr — a captive portal or an error
            // page from something in front of it — and relaying that page to the client
            // would disclose whatever it happens to contain.
            throw new SeerrApiException(HttpStatusCode.BadGateway, "Seerr returned a non-JSON response.");
        }
    }

    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                var message = GetString(obj, "message");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }

                var error = GetString(obj, "error");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return error;
                }
            }
        }
        catch (JsonException)
        {
            // A non-JSON error body is not Seerr's; returning it would forward an
            // arbitrary upstream page to the caller. Let the generic status message stand.
            return null;
        }

        return null;
    }

    private static string? GetString(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }

    private static string Sanitize(string value, string? apiKey)
    {
        return string.IsNullOrEmpty(apiKey)
            ? value
            : value.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
    }

    private sealed record SeerrTransportResult(int StatusCode, string BodyText);
}
