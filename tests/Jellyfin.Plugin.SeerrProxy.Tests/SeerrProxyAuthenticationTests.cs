using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SeerrProxy.Security;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public sealed class SeerrProxyAuthenticationTests
{
    private const string Token = "alice-user-token";
    private readonly IAuthService _auth = Substitute.For<IAuthService>();

    private static AuthorizationInfo ValidInfo() => new()
    {
        Token = Token,
        IsAuthenticated = true,
        User = new User("Alice", "Default", "Default") { Id = Guid.NewGuid() }
    };

    private static HttpRequest Request(string? token = Token)
    {
        var request = new DefaultHttpContext().Request;
        if (token is not null)
        {
            request.Headers["X-Api-Key"] = token;
        }

        return request;
    }

    [Fact]
    public async Task AlternateToken_UsesAnIsolatedNativeRequest()
    {
        var original = Request();
        var principal = original.HttpContext.User;
        var cached = new object();
        original.HttpContext.Items["AuthorizationInfo"] = cached;
        original.Headers["X-API-User"] = "999";
        var info = ValidInfo();
        _auth.Authenticate(Arg.Any<HttpRequest>()).Returns(call =>
        {
            var synthetic = call.Arg<HttpRequest>();
            Assert.NotSame(original, synthetic);
            Assert.Empty(synthetic.HttpContext.Items);
            Assert.Single(synthetic.Headers);
            Assert.Equal($"MediaBrowser Token=\"{Token}\"", synthetic.Headers.Authorization);
            return info;
        });

        Assert.Same(info, await SeerrProxyAuthentication.Authenticate(_auth, original));
        Assert.Same(principal, original.HttpContext.User);
        Assert.Same(cached, original.HttpContext.Items["AuthorizationInfo"]);
        Assert.False(original.Headers.ContainsKey("Authorization"));
        Assert.Equal(Token, original.Headers["X-Api-Key"]);
    }

    [Fact]
    public async Task NativeAuthentication_UsesTheOriginalRequest()
    {
        var request = Request(null);
        request.Headers.Authorization = $"MediaBrowser Token=\"{Token}\"";
        var info = ValidInfo();
        _auth.Authenticate(request).Returns(info);

        Assert.Same(info, await SeerrProxyAuthentication.Authenticate(_auth, request));
        await _auth.Received(1).Authenticate(request);
    }

    [Theory]
    [InlineData("missing-token")]
    [InlineData("unauthenticated")]
    [InlineData("no-user")]
    [InlineData("empty-id")]
    [InlineData("api-key-with-user")]
    [InlineData("disabled")]
    [InlineData("different-token")]
    public async Task InvalidNativeResult_IsRejected(string scenario)
    {
        var info = ValidInfo();
        switch (scenario)
        {
            case "missing-token": info.Token = null; break;
            case "unauthenticated": info.IsAuthenticated = false; break;
            case "no-user": info.User = null; break;
            case "empty-id": info.User!.Id = Guid.Empty; break;
            case "api-key-with-user": info.IsApiKey = true; break;
            case "disabled": info.User!.SetPermission(PermissionKind.IsDisabled, true); break;
            case "different-token": info.Token = "bob-user-token"; break;
        }

        _auth.Authenticate(Arg.Any<HttpRequest>()).Returns(info);
        Assert.Null(await SeerrProxyAuthentication.Authenticate(_auth, Request()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("alice,bob")]
    [InlineData("alice\r\nToken=bob")]
    [InlineData("alice\", Token=\"bob")]
    [InlineData("alice%22")]
    [InlineData("alice+bob")]
    [InlineData("álîce")]
    public async Task MalformedToken_IsRejectedBeforeAuthentication(string token)
    {
        Assert.Null(await SeerrProxyAuthentication.Authenticate(_auth, Request(token)));
        await _auth.DidNotReceive().Authenticate(Arg.Any<HttpRequest>());
    }

    [Fact]
    public async Task OversizedOrRepeatedToken_IsRejectedBeforeAuthentication()
    {
        Assert.Null(await SeerrProxyAuthentication.Authenticate(_auth, Request(new string('a', 513))));
        var request = Request();
        request.Headers["X-Api-Key"] = new StringValues([Token, Token]);
        Assert.Null(await SeerrProxyAuthentication.Authenticate(_auth, request));
        await _auth.DidNotReceive().Authenticate(Arg.Any<HttpRequest>());
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Emby-Authorization")]
    [InlineData("X-Emby-Token")]
    [InlineData("X-MediaBrowser-Token")]
    public async Task MixedHeaders_AreRejectedEvenWithTheSameToken(string header)
    {
        var request = Request();
        request.Headers[header] = Token;
        Assert.Null(await SeerrProxyAuthentication.Authenticate(_auth, request));
        await _auth.DidNotReceive().Authenticate(Arg.Any<HttpRequest>());
    }

    [Theory]
    [InlineData("?ApiKey=bob")]
    [InlineData("?api_key=bob")]
    [InlineData("?apikey=alice-user-token")]
    [InlineData("?ApiKey=")]
    public async Task MixedQueryCredentials_AreRejected(string query)
    {
        var request = Request();
        request.QueryString = new QueryString(query);
        Assert.Null(await SeerrProxyAuthentication.Authenticate(_auth, request));
        await _auth.DidNotReceive().Authenticate(Arg.Any<HttpRequest>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAuthenticationFailure_IsRejectedWithoutExposingTheException(bool securityException)
    {
        _auth.Authenticate(Arg.Any<HttpRequest>()).Throws(securityException
            ? new SecurityException(Token)
            : new AuthenticationException(Token));

        Assert.Null(await SeerrProxyAuthentication.Authenticate(_auth, Request()));
    }
}
