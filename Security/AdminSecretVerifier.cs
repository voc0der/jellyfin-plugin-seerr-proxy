using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SeerrProxy.Security;

/// <summary>
/// Verifies the operator secret that gates this plugin's elevated endpoints.
/// </summary>
/// <remarks>
/// This is the plugin's independent second gate, on top of Jellyfin's own elevation
/// policy. It exists because on Jellyfin 10.11.x <c>CustomAuthenticationHandler</c>
/// assigns the <c>Administrator</c> role to <em>any valid API key</em>, not only to
/// admin users, so every API key already issued on the server satisfies
/// <c>Policies.RequiresElevation</c> on its own. See <c>docs/SECURITY.md</c>.
/// <para>
/// It fails closed: any missing, malformed, or mismatched value is a rejection. Plain
/// SHA-256 is sufficient because the secret is a uniformly random 256-bit machine
/// value, not a human passphrase. If human-chosen passphrases are ever accepted this
/// must become a password KDF (Argon2id/bcrypt).
/// </para>
/// </remarks>
public static class AdminSecretVerifier
{
    /// <summary>
    /// The request header carrying the operator secret.
    /// </summary>
    public const string HeaderName = "X-Seerr-Proxy-Secret";

    /// <summary>
    /// Length in bytes of a SHA-256 digest.
    /// </summary>
    private const int Sha256ByteLength = 32;

    /// <summary>
    /// Verifies a presented secret against the configured hash.
    /// </summary>
    /// <param name="configuredHashHex">
    /// The configured SHA-256 hash of the operator secret, hex-encoded. A null, blank,
    /// or malformed value never verifies.
    /// </param>
    /// <param name="presentedSecret">The secret presented by the caller.</param>
    /// <returns><c>true</c> only if the presented secret matches the configured hash.</returns>
    public static bool Verify(string? configuredHashHex, string? presentedSecret)
    {
        if (string.IsNullOrEmpty(presentedSecret))
        {
            return false;
        }

        if (!TryParseConfiguredHash(configuredHashHex, out var configuredHash))
        {
            return false;
        }

        Span<byte> presentedHash = stackalloc byte[Sha256ByteLength];
        SHA256.HashData(Encoding.UTF8.GetBytes(presentedSecret), presentedHash);

        return CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash);
    }

    /// <summary>
    /// Indicates whether a usable operator secret hash is configured.
    /// </summary>
    /// <param name="configuredHashHex">The configured hash, hex-encoded.</param>
    /// <returns><c>true</c> if the configured value is a well-formed SHA-256 hash.</returns>
    public static bool IsConfigured(string? configuredHashHex)
    {
        return TryParseConfiguredHash(configuredHashHex, out _);
    }

    /// <summary>
    /// Computes the hex-encoded SHA-256 hash of a secret, in the form this plugin expects.
    /// </summary>
    /// <param name="secret">The plaintext secret.</param>
    /// <returns>The lowercase hex-encoded SHA-256 hash.</returns>
    public static string ComputeHashHex(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    private static bool TryParseConfiguredHash(string? configuredHashHex, out byte[] hash)
    {
        hash = [];

        if (string.IsNullOrWhiteSpace(configuredHashHex))
        {
            return false;
        }

        var trimmed = configuredHashHex.Trim();
        if (trimmed.Length != Sha256ByteLength * 2)
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(trimmed);
        }
        catch (FormatException)
        {
            return false;
        }

        return true;
    }
}
