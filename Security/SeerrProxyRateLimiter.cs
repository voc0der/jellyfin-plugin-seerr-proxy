using System.Threading.RateLimiting;

namespace Jellyfin.Plugin.SeerrProxy.Security;

/// <summary>
/// Bounds how often this plugin will do work, however well-credentialed the caller is.
/// </summary>
/// <remarks>
/// Authorization still applies in full; this only caps the rate. Two separate bounds,
/// because the two surfaces fail differently:
/// <list type="bullet">
/// <item>
/// the user-facing proxy is limited <em>per Jellyfin user</em>, so one client looping
/// on <c>search</c> cannot starve everyone else's requests or turn this plugin into an
/// amplifier pointed at Seerr;
/// </item>
/// <item>
/// the elevated surface is limited process-wide and much more tightly, and the limit is
/// applied <em>before</em> the operator secret is examined, so a caller holding any
/// Jellyfin API key — which Jellyfin 10.11.x treats as an administrator — cannot use
/// the endpoint to guess that secret at speed.
/// </item>
/// </list>
/// <para>
/// Both windows are process-local and reset when Jellyfin restarts. These are abuse
/// bounds, not quotas. A plugin cannot add middleware to Jellyfin's request pipeline,
/// so this uses <see cref="System.Threading.RateLimiting"/> from the ASP.NET Core shared
/// framework directly rather than the MVC rate-limiting middleware.
/// </para>
/// </remarks>
public sealed class SeerrProxyRateLimiter : IDisposable
{
    /// <summary>
    /// Proxy requests permitted per Jellyfin user per window.
    /// </summary>
    /// <remarks>
    /// Deliberately generous: a TV client opening a discover screen fans out into
    /// several calls at once, and that must not trip the limit.
    /// </remarks>
    public const int DefaultProxyPermitLimit = 120;

    /// <summary>
    /// Elevated requests permitted process-wide per window.
    /// </summary>
    /// <remarks>
    /// Tight, because the only legitimate caller is an administrator pressing a button
    /// on the configuration page. It is also the brute-force bound on the operator
    /// secret.
    /// </remarks>
    public const int DefaultAdminPermitLimit = 30;

    private readonly PartitionedRateLimiter<string> _proxyLimiter;
    private readonly FixedWindowRateLimiter _adminLimiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrProxyRateLimiter"/> class with
    /// the default per-minute limits.
    /// </summary>
    public SeerrProxyRateLimiter()
        : this(DefaultProxyPermitLimit, DefaultAdminPermitLimit, TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrProxyRateLimiter"/> class.
    /// </summary>
    /// <param name="proxyPermitLimit">Proxy requests permitted per user per window.</param>
    /// <param name="adminPermitLimit">Elevated requests permitted process-wide per window.</param>
    /// <param name="window">Length of the fixed window.</param>
    public SeerrProxyRateLimiter(int proxyPermitLimit, int adminPermitLimit, TimeSpan window)
    {
        _proxyLimiter = PartitionedRateLimiter.Create<string, string>(partitionKey =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = proxyPermitLimit,
                    Window = window,
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

        _adminLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = adminPermitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    }

    /// <summary>
    /// Attempts to take a permit for one proxied request on behalf of a Jellyfin user.
    /// </summary>
    /// <param name="jellyfinUserId">The authenticated Jellyfin user, used as the partition key.</param>
    /// <param name="retryAfter">How long the caller should wait, when refused.</param>
    /// <returns><c>true</c> if the request may proceed.</returns>
    public bool TryAcquireProxy(string jellyfinUserId, out TimeSpan retryAfter)
    {
        using var lease = _proxyLimiter.AttemptAcquire(jellyfinUserId);
        return Evaluate(lease, out retryAfter);
    }

    /// <summary>
    /// Attempts to take a permit for one elevated request.
    /// </summary>
    /// <param name="retryAfter">How long the caller should wait, when refused.</param>
    /// <returns><c>true</c> if the request may proceed.</returns>
    public bool TryAcquireAdmin(out TimeSpan retryAfter)
    {
        using var lease = _adminLimiter.AttemptAcquire();
        return Evaluate(lease, out retryAfter);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _proxyLimiter.Dispose();
        _adminLimiter.Dispose();
    }

    private static bool Evaluate(RateLimitLease lease, out TimeSpan retryAfter)
    {
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan metadata)
            ? metadata
            : TimeSpan.Zero;
        return false;
    }
}
