using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SeerrProxy.Configuration;
using Jellyfin.Plugin.SeerrProxy.Security;
using Jellyfin.Plugin.SeerrProxy.Seerr;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

/// <summary>
/// Covers the outbound half: what leaves the process, and what an upstream response is
/// allowed to turn into.
/// </summary>
public sealed class SeerrClientTests
{
    private const string BaseUrl = "http://jellyseerr:5055";
    private const string ApiKey = "configured-seerr-api-key";

    private static PluginConfiguration Config(int timeoutSeconds = 30, string? apiKey = ApiKey) => new()
    {
        Enabled = true,
        SeerrBaseUrl = BaseUrl,
        SeerrApiKey = apiKey ?? string.Empty,
        RequestTimeoutSeconds = timeoutSeconds
    };

    private static SeerrProxySecretSource SecretSource(string? environmentApiKey = null)
        => new(
            name => name == SeerrProxySecretSource.ApiKeyVariable ? environmentApiKey : null,
            _ => throw new FileNotFoundException());

    private static (SeerrClient Client, StubHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        string? environmentApiKey = null)
    {
        var handler = new StubHandler(responder);
        var client = new SeerrClient(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            SecretSource(environmentApiKey),
            NullLogger<SeerrClient>.Instance);
        return (client, handler);
    }

    private static (SeerrClient Client, StubHandler Handler) Json(
        HttpStatusCode status,
        string body,
        string? environmentApiKey = null)
        => Create((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }), environmentApiKey);

    // ---- what leaves the process ----------------------------------------------

    [Fact]
    public async Task ForwardApiRequest_SendsTheConfiguredApiKeyAndResolvedUser()
    {
        var (client, handler) = Json(HttpStatusCode.OK, """{"ok":true}""");

        await client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search?query=dune", null, default);

        var sent = Assert.Single(handler.Seen);
        Assert.Equal(ApiKey, Assert.Single(sent.Headers["X-Api-Key"]));
        Assert.Equal("7", Assert.Single(sent.Headers["X-API-User"]));
        Assert.Equal("http://jellyseerr:5055/api/v1/search?query=dune", sent.Uri);
    }

    [Fact]
    public async Task ForwardApiRequest_EnvironmentApiKeyOverridesConfiguration()
    {
        var (client, handler) = Json(HttpStatusCode.OK, "{}", environmentApiKey: "env-supplied-key");

        await client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default);

        Assert.Equal("env-supplied-key", Assert.Single(Assert.Single(handler.Seen).Headers["X-Api-Key"]));
    }

    [Fact]
    public async Task ForwardApiRequest_NoApiKeyAnywhere_IsAConfigurationError()
    {
        var (client, handler) = Json(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<SeerrConfigurationException>(
            () => client.ForwardApiRequestAsync(Config(apiKey: null), 7, HttpMethod.Get, "search", null, default));

        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task ForwardApiRequest_SendsThePayloadAsJson()
    {
        var (client, handler) = Json(HttpStatusCode.Created, """{"id":1}""");
        var payload = JsonNode.Parse("""{"mediaType":"movie","mediaId":550}""");

        await client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Post, "request", payload, default);

        var sent = Assert.Single(handler.Seen);
        Assert.Equal("""{"mediaType":"movie","mediaId":550}""", sent.Body);
        Assert.Equal("application/json", sent.ContentType);
    }

    [Fact]
    public async Task GetUserByJellyfinId_EscapesTheIdentifierIntoThePath()
    {
        var (client, handler) = Json(HttpStatusCode.OK, """{"id":7,"displayName":"Ada"}""");

        await client.GetUserByJellyfinIdAsync(Config(), "abc/../def", default);

        Assert.Equal(
            "http://jellyseerr:5055/api/v1/user/jellyfin/abc%2F..%2Fdef",
            Assert.Single(handler.Seen).Uri);
    }

    [Fact]
    public async Task GetStatus_DoesNotSendTheApiKey()
    {
        // Seerr's status endpoint is public; there is no reason to spend the credential.
        var (client, handler) = Json(HttpStatusCode.OK, """{"version":"2.1.0"}""");

        var status = await client.GetStatusAsync(Config(), default);

        Assert.Equal("2.1.0", status.Version);
        Assert.False(Assert.Single(handler.Seen).Headers.ContainsKey("X-Api-Key"));
    }

    // ---- what an upstream response may become ----------------------------------

    [Fact]
    public async Task ForwardApiRequest_PassesThroughStatusAndBody()
    {
        var (client, _) = Json(HttpStatusCode.Created, """{"id":42}""");

        var result = await client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Post, "request", null, default);

        Assert.Equal(201, result.StatusCode);
        Assert.Equal(42, result.Body!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task ForwardApiRequest_EmptyBody_YieldsNoBody()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
        {
            Content = new StringContent(string.Empty)
        }));

        var result = await client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Delete, "request/42", null, default);

        Assert.Equal(204, result.StatusCode);
        Assert.Null(result.Body);
    }

    // Redirects are not followed, so one arriving here means the base URL is wrong.
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Redirect_IsReportedAsMisconfiguration(HttpStatusCode status)
    {
        var (client, _) = Create((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
            response.Headers.Location = new Uri("https://evil.example.com/steal");
            return Task.FromResult(response);
        });

        var ex = await Assert.ThrowsAsync<SeerrConfigurationException>(
            () => client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default));

        Assert.Contains("base URL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonJsonSuccessBody_IsNotRelayedToTheCaller()
    {
        const string CaptivePortal = "<html><body>Sign in to the guest network</body></html>";
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CaptivePortal, Encoding.UTF8, "text/html")
        }));

        var ex = await Assert.ThrowsAsync<SeerrApiException>(
            () => client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.DoesNotContain("guest network", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonErrorBody_IsNotRelayedToTheCaller()
    {
        const string UpstreamPage = "<html><title>nginx internal</title>10.0.0.5</html>";
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(UpstreamPage, Encoding.UTF8, "text/html")
        }));

        var ex = await Assert.ThrowsAsync<SeerrApiException>(
            () => client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default));

        Assert.DoesNotContain("10.0.0.5", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nginx", ex.Message, StringComparison.Ordinal);
        Assert.Contains("502", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"message":"Request already exists"}""", "Request already exists")]
    [InlineData("""{"error":"Not authorised"}""", "Not authorised")]
    public async Task JsonErrorBody_ContributesItsMessage(string body, string expected)
    {
        var (client, _) = Json(HttpStatusCode.BadRequest, body);

        var ex = await Assert.ThrowsAsync<SeerrApiException>(
            () => client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default));

        Assert.Equal(expected, ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task ApiKeyEchoedInAnErrorMessage_IsRedacted()
    {
        var (client, _) = Json(HttpStatusCode.Unauthorized, $$"""{"message":"bad key {{ApiKey}}"}""");

        var ex = await Assert.ThrowsAsync<SeerrApiException>(
            () => client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default));

        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUserByJellyfinId_NonsensicalUser_IsRejected()
    {
        var (client, _) = Json(HttpStatusCode.OK, """{"id":0}""");

        var ex = await Assert.ThrowsAsync<SeerrApiException>(
            () => client.GetUserByJellyfinIdAsync(Config(), "abc", default));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
    }

    // ---- resource bounds -------------------------------------------------------

    [Fact]
    public async Task OversizedResponse_DeclaredByContentLength_IsRefused()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[9 * 1024 * 1024])
        }));

        var ex = await Assert.ThrowsAsync<SeerrApiException>(
            () => client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The header can lie or be absent, so the read loop has to enforce the bound too.
    [Fact]
    public async Task OversizedResponse_WithoutContentLength_IsRefusedWhileStreaming()
    {
        var (client, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new MemoryStream(new byte[9 * 1024 * 1024])))
        }));

        var ex = await Assert.ThrowsAsync<SeerrApiException>(
            () => client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
    }

    [Fact]
    public async Task SlowUpstream_TimesOutAsAConnectionFailure()
    {
        var (client, _) = Create(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ex = await Assert.ThrowsAsync<SeerrConnectionException>(
            () => client.ForwardApiRequestAsync(Config(timeoutSeconds: 1), 7, HttpMethod.Get, "search", null, default));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsATimeout()
    {
        var (client, _) = Create(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var caller = new CancellationTokenSource();
        var pending = client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task UnreachableUpstream_IsAConnectionFailure()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("no route to host"));

        var ex = await Assert.ThrowsAsync<SeerrConnectionException>(
            () => client.ForwardApiRequestAsync(Config(), 7, HttpMethod.Get, "search", null, default));

        Assert.Contains("unreachable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- test doubles ----------------------------------------------------------

    private sealed record SentRequest(
        string Uri,
        IReadOnlyDictionary<string, string[]> Headers,
        string? Body,
        string? ContentType);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        public List<SentRequest> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Captured eagerly: SeerrClient disposes the request once the call returns.
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Seen.Add(new SentRequest(
                request.RequestUri!.ToString(),
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                body,
                request.Content?.Headers.ContentType?.MediaType));

            return await _responder(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hides a stream's length so <see cref="StreamContent"/> cannot advertise
    /// Content-Length, forcing the bound to be enforced while reading.
    /// </summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
