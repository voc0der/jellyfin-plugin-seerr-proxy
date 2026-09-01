using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.SeerrProxy.Security;

/// <summary>
/// Decides which Seerr API calls this plugin is willing to forward.
/// </summary>
/// <remarks>
/// The allowlist is positive: a route family must be named here to be reachable, and
/// anything unrecognised is refused. It is enforced <em>before</em> the plugin resolves
/// the caller's Seerr user or touches configuration, so an unsupported path costs
/// nothing and reveals nothing.
/// <para>
/// Every segment is additionally checked against a conservative character set and
/// rejected if it is made only of dots. Kestrel already resolves <c>.</c> and <c>..</c>
/// out of a request path before routing sees it, but this plugin composes the forwarded
/// URI itself with <see cref="Uri"/> relative resolution, which <em>does</em> honour dot
/// segments — so a value that reached the route by some other decoding path must not be
/// able to climb out of Seerr's <c>/api/v1/</c> prefix. See <c>docs/SECURITY.md</c>.
/// </para>
/// </remarks>
public static partial class ApiAllowlist
{
    /// <summary>
    /// Longest query string this plugin will forward, in characters.
    /// </summary>
    /// <remarks>
    /// Seerr's own query parameters are short. The bound exists so the forwarded URI
    /// cannot be inflated into a denial-of-service payload aimed at Seerr.
    /// </remarks>
    public const int MaxQueryLength = 2048;

    /// <summary>
    /// Most path segments any allowlisted route uses, plus headroom.
    /// </summary>
    private const int MaxSegments = 8;

    /// <summary>
    /// Longest single path segment.
    /// </summary>
    private const int MaxSegmentLength = 64;

    /// <summary>
    /// Normalizes the caller-supplied path into the form the allowlist checks.
    /// </summary>
    /// <param name="path">The raw catch-all route value.</param>
    /// <returns>The path without leading or trailing slashes.</returns>
    public static string NormalizePath(string? path)
    {
        return (path ?? string.Empty).Trim('/');
    }

    /// <summary>
    /// Determines whether a method and path may be forwarded to Seerr.
    /// </summary>
    /// <param name="method">The HTTP method of the incoming request.</param>
    /// <param name="path">The normalized path under <c>/api/v1</c>.</param>
    /// <returns><c>true</c> if the request is allowlisted.</returns>
    public static bool IsAllowed(string method, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Length > MaxSegments)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (!IsSafeSegment(segment))
            {
                return false;
            }
        }

        if (HttpMethods.IsGet(method))
        {
            return IsAllowedGet(segments);
        }

        if (HttpMethods.IsPost(method))
        {
            return segments.Length == 1 && SegmentEquals(segments[0], "request");
        }

        if (HttpMethods.IsPut(method) || HttpMethods.IsDelete(method))
        {
            return IsRequestById(segments);
        }

        return false;
    }

    /// <summary>
    /// Determines whether a query string may be forwarded verbatim.
    /// </summary>
    /// <param name="query">The raw query string, including its leading <c>?</c>, or empty.</param>
    /// <returns><c>true</c> if the query is safe to append to the forwarded URI.</returns>
    public static bool IsAllowedQuery(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        if (query.Length > MaxQueryLength)
        {
            return false;
        }

        foreach (var character in query)
        {
            // A '#' would truncate the forwarded URI into a fragment, silently changing
            // which Seerr endpoint is called. Control characters have no business here
            // either; a legitimate client percent-encodes them.
            if (character == '#' || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether a single path segment is safe to compose into a URI.
    /// </summary>
    /// <param name="segment">The segment to check.</param>
    /// <returns><c>true</c> if the segment is well-formed and is not a dot segment.</returns>
    public static bool IsSafeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > MaxSegmentLength)
        {
            return false;
        }

        // "." and ".." resolve relative to the base URI and would escape /api/v1/.
        // Reject any all-dots segment rather than just those two spellings.
        if (segment.All(character => character == '.'))
        {
            return false;
        }

        return SafeSegment().IsMatch(segment);
    }

    private static bool IsAllowedGet(IReadOnlyList<string> segments)
    {
        if (SegmentsEqual(segments, "auth", "me") || SegmentsEqual(segments, "settings", "public"))
        {
            return true;
        }

        if (SegmentEquals(segments[0], "search"))
        {
            return segments.Count == 1;
        }

        if (SegmentEquals(segments[0], "discover"))
        {
            return segments.Count >= 2;
        }

        if (SegmentEquals(segments[0], "movie"))
        {
            return IsMediaDetailsPath(segments, "recommendations", "similar", "ratings", "ratingscombined");
        }

        if (SegmentEquals(segments[0], "tv"))
        {
            return IsMediaDetailsPath(segments, "recommendations", "similar", "ratings")
                || (segments.Count == 4 && IsPositiveInt(segments[1]) && SegmentEquals(segments[2], "season") && IsPositiveInt(segments[3]));
        }

        if (SegmentEquals(segments[0], "person"))
        {
            return (segments.Count == 2 && IsPositiveInt(segments[1]))
                || (segments.Count == 3 && IsPositiveInt(segments[1]) && SegmentEquals(segments[2], "combined_credits"));
        }

        if (SegmentEquals(segments[0], "request"))
        {
            return segments.Count == 1 || IsRequestById(segments);
        }

        return false;
    }

    private static bool IsMediaDetailsPath(IReadOnlyList<string> segments, params string[] allowedSubpaths)
    {
        return (segments.Count == 2 && IsPositiveInt(segments[1]))
            || (segments.Count == 3
                && IsPositiveInt(segments[1])
                && allowedSubpaths.Any(subpath => SegmentEquals(segments[2], subpath)));
    }

    private static bool IsRequestById(IReadOnlyList<string> segments)
    {
        return segments.Count == 2 && SegmentEquals(segments[0], "request") && IsPositiveInt(segments[1]);
    }

    private static bool SegmentsEqual(IReadOnlyList<string> segments, string first, string second)
    {
        return segments.Count == 2 && SegmentEquals(segments[0], first) && SegmentEquals(segments[1], second);
    }

    private static bool SegmentEquals(string actual, string expected)
    {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPositiveInt(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0;
    }

    /// <summary>
    /// The only characters an allowlisted path segment may contain.
    /// </summary>
    /// <remarks>
    /// Unreserved characters from RFC 3986 only. Notably excludes <c>%</c>, so a segment
    /// cannot smuggle a second layer of percent-encoding past this check.
    /// </remarks>
    [GeneratedRegex(@"^[A-Za-z0-9._~-]+$")]
    private static partial Regex SafeSegment();
}
