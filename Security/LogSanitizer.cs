using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SeerrProxy.Security;

/// <summary>
/// Renders caller-supplied and upstream-supplied values safe to write into a log line.
/// </summary>
/// <remarks>
/// Defence in depth, not the primary control. <see cref="ApiAllowlist"/> already
/// constrains every forwarded path segment to a conservative character set. This exists
/// so an audit line stays un-forgeable on its own terms: a log statement should not
/// depend on a validator declared in a different file to keep an attacker from
/// injecting a second line into the log (CWE-117). It also covers values this plugin
/// does not control at all, such as error text returned by Seerr.
/// </remarks>
public static partial class LogSanitizer
{
    /// <summary>
    /// Longest value written to a log line.
    /// </summary>
    private const int MaxLoggedLength = 256;

    private const string EmptyPlaceholder = "(empty)";
    private const string TruncationMarker = "...";
    private const string ReplacementCharacter = "?";

    /// <summary>
    /// Returns a value that cannot alter the structure of the log line carrying it.
    /// </summary>
    /// <param name="value">The untrusted value.</param>
    /// <returns>
    /// The value with control and format characters replaced and its length bounded, or
    /// a placeholder when it is null or empty.
    /// </returns>
    public static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return EmptyPlaceholder;
        }

        var bounded = value.Length <= MaxLoggedLength
            ? value
            : value[..MaxLoggedLength] + TruncationMarker;

        return UnsafeForLog().Replace(bounded, ReplacementCharacter);
    }

    /// <summary>
    /// Everything that can forge a log entry or disguise one.
    /// </summary>
    /// <remarks>
    /// <c>Cc</c> covers carriage returns and newlines, which start a new entry;
    /// <c>Cf</c> covers bidi overrides, which reorder how an existing one reads.
    /// <c>Zl</c> and <c>Zp</c> are added because U+2028 and U+2029 are neither, yet
    /// .NET's own <c>ReplaceLineEndings</c> and many log viewers do break lines on them.
    /// <para>
    /// Deliberately not the whole <c>C</c> group: that also covers <c>Cs</c>
    /// (surrogates), and every non-BMP character is a surrogate pair in UTF-16, so it
    /// would mangle any legitimate value containing an emoji.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]")]
    private static partial Regex UnsafeForLog();
}
