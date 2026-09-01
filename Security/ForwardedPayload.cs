using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.SeerrProxy.Security;

/// <summary>
/// Strips caller-supplied identity from a request body before it is forwarded to Seerr.
/// </summary>
/// <remarks>
/// The plugin's central promise is that the requester is derived from Jellyfin
/// authentication and from nothing else: it resolves the caller's linked Seerr user and
/// sets <c>X-API-User</c> server-side. Seerr's own request API also accepts a
/// <c>userId</c> in the body and honours it when the calling identity holds
/// <c>MANAGE_REQUESTS</c> — so a body forwarded verbatim would let a caller whose linked
/// Seerr user happens to be privileged file requests as somebody else, straight through
/// a proxy that documents the opposite.
/// <para>
/// Header identity is already safe: the plugin builds a fresh outbound
/// <c>HttpRequestMessage</c> and never copies inbound headers, so a client-supplied
/// <c>X-API-User</c>, <c>X-Api-Key</c>, or cookie cannot reach Seerr. The body is the
/// remaining channel, and this closes it.
/// </para>
/// </remarks>
public static class ForwardedPayload
{
    /// <summary>
    /// Top-level properties that name a Seerr identity and must never come from a client.
    /// </summary>
    private static readonly string[] IdentityProperties =
    [
        "userId",
        "user",
        "requestedBy",
        "modifiedBy"
    ];

    /// <summary>
    /// Removes identity-bearing properties from a forwarded payload.
    /// </summary>
    /// <param name="payload">The parsed request body, which may be null.</param>
    /// <param name="removed">The property names that were removed, for logging.</param>
    /// <returns>The payload with identity properties removed.</returns>
    public static JsonNode? StripIdentity(JsonNode? payload, out IReadOnlyList<string> removed)
    {
        if (payload is not JsonObject payloadObject)
        {
            removed = [];
            return payload;
        }

        List<string>? stripped = null;

        foreach (var identityProperty in IdentityProperties)
        {
            // Seerr's API is case-sensitive, but a client could try a differently-cased
            // spelling in the hope that some layer normalizes it. Remove every match.
            var matches = payloadObject
                .Where(property => string.Equals(property.Key, identityProperty, StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Key)
                .ToList();

            foreach (var match in matches)
            {
                payloadObject.Remove(match);
                (stripped ??= []).Add(match);
            }
        }

        removed = stripped is null ? [] : stripped;
        return payloadObject;
    }
}
