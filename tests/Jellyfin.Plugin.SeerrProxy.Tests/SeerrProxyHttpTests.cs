using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SeerrProxy.Api;
using Jellyfin.Plugin.SeerrProxy.Configuration;
using Jellyfin.Plugin.SeerrProxy.Security;
using Jellyfin.Plugin.SeerrProxy.Seerr;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

/// <summary>
/// Exercises MVC authentication/authorization and the real Seerr client with isolated
/// native authentication and upstream HTTP substitutes; no server credentials needed.
/// </summary>
[Collection("PluginInstance")]
public sealed class SeerrProxyHttpTests
{
    private const string Alice = "alice-jellyfin-token";
    private const string Bob = "bob-jellyfin-token";
    private const string SeerrKey = "server-held-seerr-key";
    private const string Secret = "operator-secret";
    private static readonly Guid AliceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid BobId = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private readonly ConcurrentQueue<SentRequest> _sent = new();
    private readonly ConcurrentQueue<string> _validated = new();

    private async Task<IHost> Start(bool enabled = true, PluginStatus status = PluginStatus.Active,
        int proxyLimit = 120, int authenticationLimit = 600)
    {
        PluginTestHost.WithConfiguration(new PluginConfiguration
        {
            Enabled = enabled,
            SeerrBaseUrl = "http://seerr.test",
            SeerrApiKey = SeerrKey
        });
        var manager = Substitute.For<IPluginManager>();
        manager.Plugins.Returns([new LocalPlugin("/plugins/seerr", true, new PluginManifest
        {
            Id = Plugin.PluginId, Name = "Seerr Proxy", Version = "1.0.0.0", Status = status
        })]);
        var auth = Substitute.For<IAuthService>();
        auth.Authenticate(Arg.Any<HttpRequest>()).Returns(call => Authenticate(call.Arg<HttpRequest>()));
        var context = Substitute.For<IAuthorizationContext>();
        context.GetAuthorizationInfo(Arg.Any<HttpContext>()).Returns(call => Authenticate(call.Arg<HttpContext>().Request));
        var secrets = new SeerrProxySecretSource(
            name => name == SeerrProxySecretSource.AdminSecretHashVariable ? AdminSecretVerifier.ComputeHashHex(Secret) : null,
            _ => throw new FileNotFoundException());

        return await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddSingleton(manager);
            services.AddSingleton(auth);
            services.AddSingleton(context);
            services.AddSingleton(secrets);
            services.AddSingleton(_ => new SeerrProxyRateLimiter(proxyLimit, 30, TimeSpan.FromMinutes(1), authenticationLimit));
            services.AddSingleton<ISeerrClient>(_ => new SeerrClient(
                new HttpClient(new Upstream(_sent)) { Timeout = Timeout.InfiniteTimeSpan }, secrets,
                NullLogger<SeerrClient>.Instance));
            services.AddAuthentication("Native").AddScheme<AuthenticationSchemeOptions, NativeHandler>("Native", _ => { });
            services.AddAuthorization(options => options.AddPolicy(Policies.RequiresElevation,
                policy => policy.RequireAuthenticatedUser().RequireRole("Administrator")));
            services.AddControllers().AddApplicationPart(typeof(SeerrProxyController).Assembly);
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        })).StartAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AuthorizationInfo> Authenticate(HttpRequest request)
    {
        // Match the native transport only: X-Api-Key alone must not authenticate in
        // middleware. Yield to exercise simultaneous synthetic requests for two users.
        await Task.Yield();
        var header = request.Headers.Authorization.ToString();
        const string prefix = "MediaBrowser Token=\"";
        if (!header.StartsWith(prefix, StringComparison.Ordinal) || !header.EndsWith('"'))
        {
            return new AuthorizationInfo();
        }

        var token = header[prefix.Length..^1];
        _validated.Enqueue(token);
        if (token is not (Alice or Bob or "unlinked" or "disabled" or "server-key" or "admin"))
        {
            throw new SecurityException("Invalid token: " + token);
        }

        if (token == "server-key")
        {
            return new AuthorizationInfo { Token = token, IsAuthenticated = true, IsApiKey = true };
        }

        var user = new User(token, "Default", "Default")
        {
            Id = token == "unlinked" ? Guid.NewGuid() : token == Bob ? BobId : AliceId
        };
        user.SetPermission(PermissionKind.IsAdministrator, token == "admin");
        user.SetPermission(PermissionKind.IsDisabled, token == "disabled");
        if (token == "disabled")
        {
            throw new SecurityException("Disabled user: " + token);
        }

        return new AuthorizationInfo { Token = token, IsAuthenticated = true, User = user };
    }

    private static HttpRequestMessage Request(string? token = Alice, string path = "api/v1/auth/me",
        bool native = false, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, "/Plugins/SeerrProxy/" + path);
        if (token is not null)
        {
            request.Headers.Add(native ? "Authorization" : "X-Api-Key", native ? $"MediaBrowser Token=\"{token}\"" : token);
        }

        return request;
    }

    [Theory]
    [InlineData(Alice, false, "api/v1/auth/me", "7")]
    [InlineData(Bob, false, "api/v1/auth/me", "8")]
    [InlineData(Alice, true, "api/v1/auth/me", "7")]
    [InlineData(Bob, true, "api/v1/auth/me", "8")]
    [InlineData(Alice, false, "api/v1/discover/movies", "7")]
    [InlineData(Bob, false, "api/v1/search?query=dune", "8")]
    public async Task UserCredential_ForwardsAsItsLinkedUser(string token, bool native, string path, string expectedUser)
    {
        using var host = await Start();
        using var request = Request(token, path, native);
        request.Headers.Add("X-API-User", "999");
        using var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedUser, _sent.Last().Headers["X-API-User"]);
        Assert.All(_sent, sent =>
        {
            Assert.Equal(SeerrKey, sent.Headers["X-Api-Key"]);
            Assert.False(sent.Headers.ContainsKey("Authorization"));
            Assert.DoesNotContain(token, sent.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(token, string.Join(" ", sent.Headers.Values), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("revoked")]
    [InlineData("disabled")]
    [InlineData("server-key")]
    public async Task UnusableCredential_IsUnauthorizedBeforeSeerr(string? token)
    {
        using var host = await Start();
        using var response = await host.GetTestClient().SendAsync(Request(token), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_sent);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(token ?? "Invalid token", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Alice)]
    [InlineData(Bob)]
    [InlineData("revoked")]
    public async Task MixedCredentials_AreRejectedWithoutChoosingAnIdentity(string nativeToken)
    {
        using var host = await Start();
        using var request = Request();
        request.Headers.Add("Authorization", $"MediaBrowser Token=\"{nativeToken}\"");
        using var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task UnlinkedUser_KeepsTheExistingResponse()
    {
        using var host = await Start();
        using var response = await host.GetTestClient().SendAsync(Request("unlinked"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("SeerrUserNotLinked", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(_sent);
    }

    [Fact]
    public async Task RequestCreation_StripsIdentityAndKeepsOnlyServerCredentials()
    {
        using var host = await Start();
        using var request = Request(path: "api/v1/request", method: HttpMethod.Post);
        request.Headers.Add("X-API-User", "999");
        request.Headers.Add("Cookie", "session=client-credential");
        request.Content = new StringContent("""{"mediaType":"movie","mediaId":550,"userId":999,"user":999,"requestedBy":999,"modifiedBy":999}""",
            Encoding.UTF8, "application/json");
        using var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = _sent.Last();
        Assert.Equal("7", sent.Headers["X-API-User"]);
        Assert.Equal(SeerrKey, sent.Headers["X-Api-Key"]);
        Assert.False(sent.Headers.ContainsKey("Cookie"));
        Assert.Equal("""{"mediaType":"movie","mediaId":550}""", sent.Body);
    }

    [Theory]
    [InlineData(false, PluginStatus.Active, HttpStatusCode.Forbidden)]
    [InlineData(true, PluginStatus.Disabled, HttpStatusCode.NotFound)]
    [InlineData(true, PluginStatus.Restart, HttpStatusCode.NotFound)]
    public async Task DisabledPlugin_CannotForward(bool enabled, PluginStatus status, HttpStatusCode expected)
    {
        using var host = await Start(enabled, status);
        using var response = await host.GetTestClient().SendAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData("Status", "GET", Alice, false, true, HttpStatusCode.Unauthorized)]
    [InlineData("Test", "POST", "admin", false, true, HttpStatusCode.Unauthorized)]
    [InlineData("Test", "POST", Alice, true, true, HttpStatusCode.Forbidden)]
    [InlineData("Test", "POST", "admin", true, false, HttpStatusCode.Forbidden)]
    [InlineData("Test", "POST", "admin", true, true, HttpStatusCode.OK)]
    [InlineData("Status", "GET", Alice, true, false, HttpStatusCode.OK)]
    public async Task NormalRoutes_KeepTheirAuthorizationAndOperatorGates(string path, string method,
        string token, bool native, bool secret, HttpStatusCode expected)
    {
        using var host = await Start();
        using var request = Request(token, path, native, new HttpMethod(method));
        if (secret)
        {
            request.Headers.Add(AdminSecretVerifier.HeaderName, Secret);
        }

        using var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK)
        {
            Assert.Empty(_sent);
        }
    }

    [Fact]
    public async Task ConcurrentUsers_DoNotShareIdentity()
    {
        using var host = await Start();
        using var client = host.GetTestClient();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async index =>
        {
            var alice = index % 2 == 0;
            using var request = Request(alice ? Alice : Bob);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(alice ? "7" : "8", json!["actingUser"]!.GetValue<string>());
        }));
    }

    [Fact]
    public async Task InvalidAttempts_AreBoundedDespiteChangingTokensAndForwardingHeaders()
    {
        using var host = await Start(authenticationLimit: 1);
        using var client = host.GetTestClient();
        using var first = await client.SendAsync(Request("invalid"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        using var next = Request("another-invalid-token");
        next.Headers.Add("X-Forwarded-For", "192.0.2.2");
        next.Headers.Add("X-Real-IP", "192.0.2.3");
        using var second = await client.SendAsync(next, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        Assert.Equal(["invalid"], _validated.ToArray());
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AlternateAndNativeTokens_ShareThePerUserLimit()
    {
        using var host = await Start(proxyLimit: 1);
        using var client = host.GetTestClient();
        using var first = await client.SendAsync(Request(), TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(Request(native: true), TestContext.Current.CancellationToken);
        using var bob = await client.SendAsync(Request(Bob), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, bob.StatusCode);
        Assert.Equal(4, _sent.Count); // One mapping and one forwarded request per permitted user.
    }

    private sealed record SentRequest(string Path, Dictionary<string, string> Headers, string? Body);

    private sealed class Upstream(ConcurrentQueue<SentRequest> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            sent.Enqueue(new SentRequest(path, headers,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (path.Contains("/user/jellyfin/", StringComparison.Ordinal))
            {
                var id = path.EndsWith(AliceId.ToString("N"), StringComparison.Ordinal) ? 7
                    : path.EndsWith(BobId.ToString("N"), StringComparison.Ordinal) ? 8 : 0;
                return Json(id == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK, $"{{\"id\":{id}}}");
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["actingUser"] = headers.GetValueOrDefault("X-API-User") }.ToJsonString());
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    // A native-only scheme makes the middleware rejection that motivated this patch
    // observable, while leaving Jellyfin's credential validation mocked and isolated.
    private sealed class NativeHandler(IAuthService auth, IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            try
            {
                var info = await auth.Authenticate(Request);
                if (!info.HasToken || !info.IsAuthenticated)
                {
                    return AuthenticateResult.NoResult();
                }

                var role = info.IsApiKey || info.User!.HasPermission(PermissionKind.IsAdministrator) ? "Administrator" : "User";
                var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], Scheme.Name);
                return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
            }
            catch (SecurityException)
            {
                return AuthenticateResult.NoResult();
            }
        }
    }
}
