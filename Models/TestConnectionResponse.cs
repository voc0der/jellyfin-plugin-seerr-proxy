namespace Jellyfin.Plugin.SeerrProxy.Models;

/// <summary>
/// Response from the Seerr connection test endpoint.
/// </summary>
public sealed class TestConnectionResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether Seerr status was reachable.
    /// </summary>
    public bool Reachable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the configured API key was accepted.
    /// </summary>
    public bool Authenticated { get; set; }

    /// <summary>
    /// Gets or sets the Seerr version when available.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets where the Seerr API key came from.
    /// </summary>
    /// <remarks>
    /// Either <c>environment</c> or <c>configuration</c>. Names the source only — never
    /// the key, or any part of it.
    /// </remarks>
    public string? ApiKeySource { get; set; }

    /// <summary>
    /// Gets or sets a displayable message.
    /// </summary>
    public string? Message { get; set; }
}
