using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SeerrProxy.Api;
using Jellyfin.Plugin.SeerrProxy.Configuration;
using Jellyfin.Plugin.SeerrProxy.Models;
using Jellyfin.Plugin.SeerrProxy.Security;
using Jellyfin.Plugin.SeerrProxy.Seerr;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

/// <summary>
/// Covers gate ordering and the mapping from Seerr's behaviour to HTTP results.
/// </summary>
/// <remarks>
/// Every test here touches the process-wide <c>Plugin.Instance</c> through
/// <see cref="PluginTestHost"/>. xUnit runs the tests within one class sequentially, and
/// the shared collection keeps <see cref="PluginTests"/> — which also constructs a plugin
/// — from running alongside them.
/// </remarks>
[Collection("PluginInstance")]
public sealed class SeerrProxyControllerTests
{
    private const string Secret = "test-operator-secret-value";
    private const string BaseUrl = "http://jellyseerr:5055";
    private const string ApiKey = "configured-seerr-api-key";
    private const int SeerrUserId = 7;

    private static readonly Guid JellyfinUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string JellyfinUserIdText = JellyfinUserId.ToString("N");

    private readonly ISeerrClient _seerrClient = Substitute.For<ISeerrClient>();
    private readonly IAuthorizationContext _authorizationContext = Substitute.For<IAuthorizationContext>();
    private readonly IPluginManager _pluginManager = Substitute.For<IPluginManager>();

    public SeerrProxyControllerTests()
    {
        SetPluginStatus(PluginStatus.Active);
        SetAuthenticatedUser(JellyfinUserId);

        _seerrClient.GetUserByJellyfinIdAsync(Arg.Any<PluginConfiguration>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SeerrUser { Id = SeerrUserId, DisplayName = "Ada" });

        _seerrClient.ForwardApiRequestAsync(
                Arg.Any<PluginConfiguration>(),
                Arg.Any<int>(),
                Arg.Any<HttpMethod>(),
                Arg.Any<string>(),
                Arg.Any<JsonNode?>(),
                Arg.Any<CancellationToken>())
            .Returns(new SeerrApiResult(200, JsonNode.Parse("""{"ok":true}""")));

        _seerrClient.GetStatusAsync(Arg.Any<PluginConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(new SeerrStatus { Version = "2.1.0" });
    }

    // ---- harness ---------------------------------------------------------------

    private static PluginManifest ManifestWith(PluginStatus status) => new()
    {
        Id = Plugin.PluginId,
        Name = "Seerr Proxy",
        Version = "1.0.0.0",
        Status = status
    };

    private void SetPluginStatus(PluginStatus status, bool isSupported = true)
        => _pluginManager.Plugins.Returns(new[] { new LocalPlugin("/plugins/seerr", isSupported, ManifestWith(status)) });

    private void SetNoPluginRecord()
        => _pluginManager.Plugins.Returns(Array.Empty<LocalPlugin>());

    private void SetAuthenticatedUser(Guid userId)
    {
        // AuthorizationInfo.UserId is computed as User?.Id ?? Guid.Empty, so the only way
        // to present a real user is to attach one.
        var info = new AuthorizationInfo
        {
            IsAuthenticated = true,
            User = new User("ada", "Default", "Default") { Id = userId }
        };
        _authorizationContext.GetAuthorizationInfo(Arg.Any<HttpContext>()).Returns(info);
    }

    /// <summary>
    /// An API key authenticates without being a user, and Jellyfin 10.11.x hands it the
    /// Administrator role.
    /// </summary>
    private void SetApiKeyCaller()
        => _authorizationContext.GetAuthorizationInfo(Arg.Any<HttpContext>())
            .Returns(new AuthorizationInfo { IsAuthenticated = true, IsApiKey = true });

    private static PluginConfiguration Configured(bool enabled = true, string? apiKey = ApiKey, string? baseUrl = BaseUrl)
        => new()
        {
            Enabled = enabled,
            SeerrBaseUrl = baseUrl ?? string.Empty,
            SeerrApiKey = apiKey ?? string.Empty
        };

    private SeerrProxyController CreateController(
        PluginConfiguration? configuration = null,
        SeerrProxyRateLimiter? rateLimiter = null,
        string? configuredHash = null,
        bool requireSecret = false,
        string? presentedSecret = null,
        string method = "GET",
        string query = "",
        string? body = null)
    {
        PluginTestHost.WithConfiguration(configuration ?? Configured());

        var secretSource = new SeerrProxySecretSource(
            name => name switch
            {
                SeerrProxySecretSource.AdminSecretHashVariable => configuredHash,
                SeerrProxySecretSource.RequireAdminSecretVariable => requireSecret ? "1" : null,
                _ => null
            },
            _ => throw new FileNotFoundException());

        var controller = new SeerrProxyController(
            _seerrClient,
            _authorizationContext,
            _pluginManager,
            secretSource,
            rateLimiter ?? new SeerrProxyRateLimiter(1000, 1000, TimeSpan.FromMinutes(1)),
            NullLogger<SeerrProxyController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.QueryString = new QueryString(query);
        if (presentedSecret is not null)
        {
            httpContext.Request.Headers[AdminSecretVerifier.HeaderName] = presentedSecret;
        }

        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Request.ContentLength = bytes.Length;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        StatusCodeResult s => s.StatusCode,
        ObjectResult o => o.StatusCode ?? 0,
        _ => 0
    };

    private static int StatusOf<T>(ActionResult<T> result) => result.Result is null ? 200 : StatusOf(result.Result);

    private static string ErrorCodeOf(IActionResult result)
        => Assert.IsType<ErrorResponse>(Assert.IsType<ObjectResult>(result, exactMatch: false).Value).Error;

    private Task AssertNothingForwarded()
        => _seerrClient.DidNotReceive().ForwardApiRequestAsync(
            Arg.Any<PluginConfiguration>(),
            Arg.Any<int>(),
            Arg.Any<HttpMethod>(),
            Arg.Any<string>(),
            Arg.Any<JsonNode?>(),
            Arg.Any<CancellationToken>());

    // ---- the plugin kill switch ------------------------------------------------

    [Theory]
    [InlineData(PluginStatus.Disabled)]
    [InlineData(PluginStatus.Malfunctioned)]
    [InlineData(PluginStatus.NotSupported)]
    [InlineData(PluginStatus.Superseded)]
    [InlineData(PluginStatus.Deleted)]
    [InlineData(PluginStatus.Restart)] // queued for disable: still "enabled and supported"
    public async Task EveryEndpoint_PluginNotActive_IsRefused(PluginStatus status)
    {
        SetPluginStatus(status);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().GetStatus(default)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().ForwardApiRequest("search", default)));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController(method: "POST").TestConnection(default)));
        await AssertNothingForwarded();
    }

    [Fact]
    public async Task ForwardApiRequest_PluginUnsupported_IsRefused()
    {
        SetPluginStatus(PluginStatus.Active, isSupported: false);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CreateController().ForwardApiRequest("search", default)));
        await AssertNothingForwarded();
    }

    // Fail closed: an assembly serving requests with no matching plugin record is in an
    // unexpected state, so it must refuse rather than assume it is fine.
    [Fact]
    public async Task ForwardApiRequest_NoPluginRecord_IsRefused()
    {
        SetNoPluginRecord();

        var result = await CreateController().ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal("PluginInactive", ErrorCodeOf(result));
        await AssertNothingForwarded();
    }

    // ---- an API key is not a user ----------------------------------------------

    [Fact]
    public async Task ForwardApiRequest_ApiKeyCaller_IsUnauthorized()
    {
        SetApiKeyCaller();

        var result = await CreateController().ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal("MissingJellyfinUser", ErrorCodeOf(result));
        await AssertNothingForwarded();
        await _seerrClient.DidNotReceive().GetUserByJellyfinIdAsync(
            Arg.Any<PluginConfiguration>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatus_ApiKeyCaller_IsUnauthorized()
    {
        SetApiKeyCaller();

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(await CreateController().GetStatus(default)));
    }

    // ---- the allowlist ---------------------------------------------------------

    [Theory]
    [InlineData("GET", "search")]
    [InlineData("GET", "discover/movies")]
    [InlineData("GET", "request/42")]
    [InlineData("POST", "request")]
    [InlineData("PUT", "request/42")]
    [InlineData("DELETE", "request/42")]
    public async Task ForwardApiRequest_AllowlistedRoute_IsForwarded(string method, string path)
    {
        var controller = CreateController(method: method, body: method is "POST" or "PUT" ? "{}" : null);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.ForwardApiRequest(path, default)));

        await _seerrClient.Received(1).ForwardApiRequestAsync(
            Arg.Any<PluginConfiguration>(),
            SeerrUserId,
            Arg.Is<HttpMethod>(m => m.Method == method),
            path,
            Arg.Any<JsonNode?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("GET", "user/1")]
    [InlineData("GET", "settings/main")]
    [InlineData("GET", "discover/../../user/1")]
    [InlineData("GET", "discover/movies%2f..%2fuser")]
    [InlineData("POST", "user")]
    [InlineData("DELETE", "request")]
    public async Task ForwardApiRequest_RouteOutsideTheAllowlist_IsNotFound(string method, string path)
    {
        var controller = CreateController(method: method, body: method == "POST" ? "{}" : null);

        var result = await controller.ForwardApiRequest(path, default);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal("UnsupportedProxyEndpoint", ErrorCodeOf(result));
        await AssertNothingForwarded();
    }

    [Theory]
    [InlineData("?a=1#b")]
    [InlineData("?a= ")]
    public async Task ForwardApiRequest_UnsafeQuery_IsNotFound(string query)
    {
        var result = await CreateController(query: query).ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        await AssertNothingForwarded();
    }

    [Fact]
    public async Task ForwardApiRequest_QueryIsPassedThroughWithThePath()
    {
        var controller = CreateController(query: "?query=dune&page=2");

        await controller.ForwardApiRequest("search", default);

        await _seerrClient.Received(1).ForwardApiRequestAsync(
            Arg.Any<PluginConfiguration>(),
            SeerrUserId,
            Arg.Any<HttpMethod>(),
            "search?query=dune&page=2",
            Arg.Any<JsonNode?>(),
            Arg.Any<CancellationToken>());
    }

    // The allowlist runs before configuration is read, so an unsupported path reveals
    // nothing about whether the plugin is set up.
    [Fact]
    public async Task ForwardApiRequest_AllowlistRunsBeforeTheEnabledCheck()
    {
        var controller = CreateController(Configured(enabled: false));

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.ForwardApiRequest("user/1", default)));
    }

    // ---- enabled and configured ------------------------------------------------

    [Fact]
    public async Task ForwardApiRequest_PluginDisabled_IsForbidden()
    {
        var result = await CreateController(Configured(enabled: false)).ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Equal("PluginDisabled", ErrorCodeOf(result));
        await AssertNothingForwarded();
    }

    [Theory]
    [InlineData(null, BaseUrl)]
    [InlineData(ApiKey, null)]
    [InlineData(null, null)]
    public async Task ForwardApiRequest_NotConfigured_IsServiceUnavailable(string? apiKey, string? baseUrl)
    {
        var controller = CreateController(Configured(apiKey: apiKey, baseUrl: baseUrl));

        var result = await controller.ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(result));
        Assert.Equal("PluginNotConfigured", ErrorCodeOf(result));
        await AssertNothingForwarded();
    }

    // ---- identity stripping ----------------------------------------------------

    [Fact]
    public async Task ForwardApiRequest_StripsClientSuppliedUserId()
    {
        var controller = CreateController(
            method: "POST",
            body: """{"mediaType":"movie","mediaId":550,"userId":99}""");

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.ForwardApiRequest("request", default)));

        await _seerrClient.Received(1).ForwardApiRequestAsync(
            Arg.Any<PluginConfiguration>(),
            SeerrUserId,
            Arg.Any<HttpMethod>(),
            "request",
            Arg.Is<JsonNode?>(node => node!["userId"] == null && node["mediaId"]!.GetValue<int>() == 550),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForwardApiRequest_ActsAsTheResolvedUserNotTheRequestedOne()
    {
        var controller = CreateController(method: "POST", body: """{"userId":99}""");

        await controller.ForwardApiRequest("request", default);

        // The acting identity is the Seerr user resolved from Jellyfin auth, never 99.
        await _seerrClient.Received(1).ForwardApiRequestAsync(
            Arg.Any<PluginConfiguration>(),
            SeerrUserId,
            Arg.Any<HttpMethod>(),
            Arg.Any<string>(),
            Arg.Any<JsonNode?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForwardApiRequest_ResolvesTheSeerrUserFromJellyfinAuthentication()
    {
        await CreateController().ForwardApiRequest("search", default);

        await _seerrClient.Received(1).GetUserByJellyfinIdAsync(
            Arg.Any<PluginConfiguration>(), JellyfinUserIdText, Arg.Any<CancellationToken>());
    }

    // ---- request bodies --------------------------------------------------------

    [Fact]
    public async Task ForwardApiRequest_OversizedBody_IsRefused()
    {
        var oversized = "{\"pad\":\"" + new string('a', 300 * 1024) + "\"}";
        var controller = CreateController(method: "POST", body: oversized);

        var result = await controller.ForwardApiRequest("request", default);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, StatusOf(result));
        Assert.Equal("RequestBodyTooLarge", ErrorCodeOf(result));
        await AssertNothingForwarded();
    }

    [Fact]
    public async Task ForwardApiRequest_MalformedJson_IsBadRequest()
    {
        var controller = CreateController(method: "POST", body: "{not json");

        var result = await controller.ForwardApiRequest("request", default);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal("InvalidJson", ErrorCodeOf(result));
        await AssertNothingForwarded();
    }

    [Fact]
    public async Task ForwardApiRequest_EmptyBody_ForwardsNoPayload()
    {
        var controller = CreateController(method: "POST", body: "");

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.ForwardApiRequest("request", default)));

        await _seerrClient.Received(1).ForwardApiRequestAsync(
            Arg.Any<PluginConfiguration>(),
            SeerrUserId,
            Arg.Any<HttpMethod>(),
            "request",
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForwardApiRequest_GetRequest_NeverReadsABody()
    {
        var controller = CreateController(body: """{"userId":99}""");

        await controller.ForwardApiRequest("search", default);

        await _seerrClient.Received(1).ForwardApiRequestAsync(
            Arg.Any<PluginConfiguration>(),
            SeerrUserId,
            Arg.Any<HttpMethod>(),
            "search",
            null,
            Arg.Any<CancellationToken>());
    }

    // ---- rate limiting ---------------------------------------------------------

    [Fact]
    public async Task ForwardApiRequest_OverRateLimit_Returns429WithRetryAfter()
    {
        using var limiter = new SeerrProxyRateLimiter(1, 1000, TimeSpan.FromMinutes(1));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await CreateController(rateLimiter: limiter).ForwardApiRequest("search", default)));

        var second = CreateController(rateLimiter: limiter);
        var result = await second.ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(result));
        Assert.Equal("RateLimited", ErrorCodeOf(result));
        Assert.False(string.IsNullOrEmpty(second.Response.Headers.RetryAfter));
    }

    [Fact]
    public async Task ForwardApiRequest_OneUserExhaustingTheLimitDoesNotBlockAnother()
    {
        using var limiter = new SeerrProxyRateLimiter(1, 1000, TimeSpan.FromMinutes(1));

        await CreateController(rateLimiter: limiter).ForwardApiRequest("search", default);
        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            StatusOf(await CreateController(rateLimiter: limiter).ForwardApiRequest("search", default)));

        SetAuthenticatedUser(Guid.Parse("99999999-8888-7777-6666-555555555555"));

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await CreateController(rateLimiter: limiter).ForwardApiRequest("search", default)));
    }

    // 429 rather than any outbound call proves a flood is bounded before Seerr is touched.
    [Fact]
    public async Task ForwardApiRequest_RateLimitIsCheckedBeforeReachingSeerr()
    {
        using var limiter = new SeerrProxyRateLimiter(1, 1000, TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquireProxy(JellyfinUserIdText, out _)); // exhaust it

        var result = await CreateController(rateLimiter: limiter).ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(result));
        await AssertNothingForwarded();
    }

    // ---- the operator secret ---------------------------------------------------

    [Fact]
    public async Task TestConnection_NoHashConfigured_FallsBackToElevationAlone()
    {
        var controller = CreateController(method: "POST", configuredHash: null);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.TestConnection(default)));
    }

    [Fact]
    public async Task TestConnection_HashConfiguredAndSecretPresented_Succeeds()
    {
        var controller = CreateController(
            method: "POST",
            configuredHash: AdminSecretVerifier.ComputeHashHex(Secret),
            presentedSecret: Secret);

        var result = await controller.TestConnection(default);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        var body = Assert.IsType<TestConnectionResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Reachable);
        Assert.True(body.Authenticated);
        Assert.Equal("2.1.0", body.Version);
        Assert.Equal("configuration", body.ApiKeySource);
    }

    [Theory]
    [InlineData(null)]              // header absent entirely
    [InlineData("")]
    [InlineData("wrong-secret")]
    [InlineData("test-operator-secret-valu")] // one character short
    public async Task TestConnection_HashConfiguredAndSecretWrong_IsForbidden(string? presented)
    {
        var controller = CreateController(
            method: "POST",
            configuredHash: AdminSecretVerifier.ComputeHashHex(Secret),
            presentedSecret: presented);

        var result = await controller.TestConnection(default);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Equal("OperatorSecretRequired", ErrorCodeOf(result.Result!));
        await _seerrClient.DidNotReceive().GetStatusAsync(Arg.Any<PluginConfiguration>(), Arg.Any<CancellationToken>());
    }

    // A hash file that fails to mount must not silently downgrade the gate.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("deadbeef")]
    public async Task TestConnection_SecretRequiredButHashUnusable_IsForbidden(string? configuredHash)
    {
        var controller = CreateController(
            method: "POST",
            configuredHash: configuredHash,
            requireSecret: true,
            presentedSecret: Secret);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.TestConnection(default)));
    }

    [Fact]
    public async Task TestConnection_RateLimitIsCheckedBeforeTheSecret()
    {
        using var limiter = new SeerrProxyRateLimiter(1000, 1, TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquireAdmin(out _)); // exhaust it

        var controller = CreateController(
            method: "POST",
            rateLimiter: limiter,
            configuredHash: AdminSecretVerifier.ComputeHashHex(Secret),
            presentedSecret: "wrong-secret");

        // 429 rather than 403 proves guessing is bounded before any comparison happens.
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(await controller.TestConnection(default)));
    }

    [Fact]
    public async Task TestConnection_PluginStateIsCheckedBeforeTheRateLimit()
    {
        SetPluginStatus(PluginStatus.Disabled);
        using var limiter = new SeerrProxyRateLimiter(1000, 1, TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquireAdmin(out _));

        var controller = CreateController(method: "POST", rateLimiter: limiter);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.TestConnection(default)));
    }

    // Deliberate: an administrator must be able to verify a connection before enabling
    // the proxy, so Test does not require Enabled.
    [Fact]
    public async Task TestConnection_PluginDisabledInConfiguration_StillRuns()
    {
        var controller = CreateController(Configured(enabled: false), method: "POST");

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.TestConnection(default)));
    }

    [Fact]
    public async Task TestConnection_NotConfigured_IsServiceUnavailable()
    {
        var controller = CreateController(Configured(apiKey: null), method: "POST");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(await controller.TestConnection(default)));
    }

    // ---- error mapping ---------------------------------------------------------

    [Fact]
    public async Task ForwardApiRequest_UserNotLinked_IsNotFoundWithADistinctCode()
    {
        _seerrClient.GetUserByJellyfinIdAsync(Arg.Any<PluginConfiguration>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new SeerrApiException(HttpStatusCode.NotFound, "not found"));

        var result = await CreateController().ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal("SeerrUserNotLinked", ErrorCodeOf(result));
    }

    [Fact]
    public async Task ForwardApiRequest_SeerrUnreachable_IsBadGateway()
    {
        _seerrClient.GetUserByJellyfinIdAsync(Arg.Any<PluginConfiguration>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new SeerrConnectionException("down", new HttpRequestException()));

        var result = await CreateController().ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status502BadGateway, StatusOf(result));
        Assert.Equal("SeerrUnreachable", ErrorCodeOf(result));
    }

    [Fact]
    public async Task ForwardApiRequest_MisconfiguredUpstream_IsServiceUnavailable()
    {
        _seerrClient.GetUserByJellyfinIdAsync(Arg.Any<PluginConfiguration>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new SeerrConfigurationException("bad base url"));

        var result = await CreateController().ForwardApiRequest("search", default);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(result));
        Assert.Equal("PluginNotConfigured", ErrorCodeOf(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(HttpStatusCode.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(HttpStatusCode.Conflict, StatusCodes.Status409Conflict)]
    public async Task ForwardApiRequest_UpstreamClientError_IsPassedThrough(HttpStatusCode upstream, int expected)
    {
        _seerrClient.ForwardApiRequestAsync(
                Arg.Any<PluginConfiguration>(), Arg.Any<int>(), Arg.Any<HttpMethod>(),
                Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<CancellationToken>())
            .Throws(new SeerrApiException(upstream, "upstream said no"));

        Assert.Equal(expected, StatusOf(await CreateController().ForwardApiRequest("search", default)));
    }

    // A caller must not be able to tell Seerr's internal failures apart.
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ForwardApiRequest_UpstreamServerError_CollapsesToBadGateway(HttpStatusCode upstream)
    {
        _seerrClient.ForwardApiRequestAsync(
                Arg.Any<PluginConfiguration>(), Arg.Any<int>(), Arg.Any<HttpMethod>(),
                Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<CancellationToken>())
            .Throws(new SeerrApiException(upstream, "boom"));

        Assert.Equal(StatusCodes.Status502BadGateway, StatusOf(await CreateController().ForwardApiRequest("search", default)));
    }

    [Fact]
    public async Task ForwardApiRequest_EmptyUpstreamBody_ReturnsBareStatus()
    {
        _seerrClient.ForwardApiRequestAsync(
                Arg.Any<PluginConfiguration>(), Arg.Any<int>(), Arg.Any<HttpMethod>(),
                Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<CancellationToken>())
            .Returns(new SeerrApiResult(StatusCodes.Status204NoContent, null));

        var result = await CreateController(method: "DELETE").ForwardApiRequest("request/42", default);

        Assert.Equal(StatusCodes.Status204NoContent, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    // ---- status ----------------------------------------------------------------

    [Fact]
    public async Task GetStatus_LinkedUser_ReportsTheLink()
    {
        var result = await CreateController().GetStatus(default);

        var body = Assert.IsType<SeerrProxyStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Enabled);
        Assert.True(body.Configured);
        Assert.True(body.Linked);
        Assert.True(body.SeerrReachable);
        Assert.Equal(SeerrUserId, body.SeerrUserId);
        Assert.Equal("Ada", body.DisplayName);
        Assert.Equal(JellyfinUserIdText, body.JellyfinUserId);
    }

    [Fact]
    public async Task GetStatus_UnlinkedUser_ReportsReachableButUnlinked()
    {
        _seerrClient.GetUserByJellyfinIdAsync(Arg.Any<PluginConfiguration>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new SeerrApiException(HttpStatusCode.NotFound, "not found"));

        var result = await CreateController().GetStatus(default);

        var body = Assert.IsType<SeerrProxyStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.Linked);
        Assert.True(body.SeerrReachable);
        Assert.Null(body.SeerrUserId);
    }

    [Fact]
    public async Task GetStatus_SeerrDown_ReportsUnreachableWithoutFailingTheRequest()
    {
        _seerrClient.GetUserByJellyfinIdAsync(Arg.Any<PluginConfiguration>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new SeerrConnectionException("down", new HttpRequestException()));

        var result = await CreateController().GetStatus(default);

        var body = Assert.IsType<SeerrProxyStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.SeerrReachable);
        Assert.Equal("Seerr is unreachable.", body.MappingError);
    }

    [Theory]
    [InlineData(false, ApiKey)]
    [InlineData(true, null)]
    public async Task GetStatus_DisabledOrUnconfigured_DoesNotContactSeerr(bool enabled, string? apiKey)
    {
        var controller = CreateController(Configured(enabled: enabled, apiKey: apiKey));

        var result = await controller.GetStatus(default);

        var body = Assert.IsType<SeerrProxyStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(enabled, body.Enabled);
        Assert.Equal(apiKey is not null, body.Configured);
        Assert.Null(body.Linked);
        await _seerrClient.DidNotReceive().GetUserByJellyfinIdAsync(
            Arg.Any<PluginConfiguration>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatus_NeverExposesTheApiKey()
    {
        var result = await CreateController().GetStatus(default);

        var serialized = System.Text.Json.JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.DoesNotContain(ApiKey, serialized, StringComparison.Ordinal);
    }
}
