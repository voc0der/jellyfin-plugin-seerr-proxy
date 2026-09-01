using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SeerrProxy.Seerr;

/// <summary>
/// Seerr user projection used internally by the proxy.
/// </summary>
public sealed class SeerrUser
{
    /// <summary>
    /// Gets or sets the Seerr user ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the Seerr display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the local Seerr username.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the linked Jellyfin username.
    /// </summary>
    [JsonPropertyName("jellyfinUsername")]
    public string? JellyfinUsername { get; set; }

    /// <summary>
    /// Gets or sets the linked Plex username.
    /// </summary>
    [JsonPropertyName("plexUsername")]
    public string? PlexUsername { get; set; }

    /// <summary>
    /// Gets a non-sensitive display name.
    /// </summary>
    /// <returns>Best available display name.</returns>
    public string? GetSafeDisplayName()
    {
        return FirstNonEmpty(DisplayName, Username, JellyfinUsername, PlexUsername);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
