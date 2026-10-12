using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SeerrProxy.Security;

/// <summary>
/// Validates a Jellyfin user credential for the proxy without changing host authentication.
/// </summary>
internal static class SeerrProxyAuthentication
{
    private const string AlternateHeader = "X-Api-Key";
    private const int MaxTokenLength = 512;

    private static readonly string[] NativeHeaders =
        ["Authorization", "X-Emby-Authorization", "X-Emby-Token", "X-MediaBrowser-Token"];

    public static async Task<AuthorizationInfo?> Authenticate(IAuthService authService, HttpRequest request)
    {
        var authenticationRequest = request;
        string? alternateToken = null;
        if (request.Headers.TryGetValue(AlternateHeader, out var values))
        {
            // Use exactly one transport. Even equal credentials in both transports are
            // refused, avoiding dependence on Jellyfin's header/query precedence or
            // legacy-authorization setting. Never select a credential that grants access.
            if (NativeHeaders.Any(request.Headers.ContainsKey)
                || request.Query.ContainsKey("ApiKey")
                || request.Query.ContainsKey("api_key")
                || values.Count != 1)
            {
                return null;
            }

            alternateToken = values[0];
            // Bound the input and exclude quoting, separators, URL decoding and control
            // characters before constructing a native authorization header. Jellyfin
            // user access tokens fit this unreserved ASCII alphabet.
            if (string.IsNullOrEmpty(alternateToken)
                || alternateToken.Length > MaxTokenLength
                || alternateToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.' and not '_' and not '~'))
            {
                return null;
            }

            authenticationRequest = new DefaultHttpContext().Request;
            authenticationRequest.Headers.Authorization = $"MediaBrowser Token=\"{alternateToken}\"";
        }

        try
        {
            // IAuthService performs native token validation and disabled-user checks.
            // Do not use cached authorization info or attach the result to the real
            // request: neither its principal nor its Items/headers belong to this gate.
            var info = await authService.Authenticate(authenticationRequest).ConfigureAwait(false);
            return info is not null
                && info.HasToken
                && info.IsAuthenticated
                && !info.IsApiKey
                && info.User is not null
                && info.UserId != Guid.Empty
                && !info.User.HasPermission(PermissionKind.IsDisabled)
                && (alternateToken is null || string.Equals(info.Token, alternateToken, StringComparison.Ordinal))
                    ? info
                    : null;
        }
        catch (AuthenticationException)
        {
            return null;
        }
        catch (SecurityException)
        {
            // Native failures can contain credential details. Never log or return them.
            return null;
        }
    }
}
