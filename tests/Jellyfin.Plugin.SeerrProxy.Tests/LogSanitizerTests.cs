using Jellyfin.Plugin.SeerrProxy.Security;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class LogSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ForLog_NullOrEmpty_IsPlaceholder(string? value)
    {
        Assert.Equal("(empty)", LogSanitizer.ForLog(value));
    }

    [Fact]
    public void ForLog_OrdinaryValue_IsUnchanged()
    {
        Assert.Equal("discover/movies", LogSanitizer.ForLog("discover/movies"));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u202Eb")]
    [InlineData("a\0b")]
    public void ForLog_LineBreakingAndReorderingCharacters_AreReplaced(string value)
    {
        Assert.Equal("a?b", LogSanitizer.ForLog(value));
    }

    [Fact]
    public void ForLog_ForgedSecondEntry_CannotSurvive()
    {
        var forged = "movie\n2026-09-01 00:00:00 [INF] Seerr Proxy: access granted";

        var sanitized = LogSanitizer.ForLog(forged);

        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\r', sanitized);
    }

    [Fact]
    public void ForLog_OverlongValue_IsTruncated()
    {
        var sanitized = LogSanitizer.ForLog(new string('a', 500));

        Assert.Equal(259, sanitized.Length);
        Assert.EndsWith("...", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void ForLog_NonBmpCharacters_SurviveIntact()
    {
        // Emoji are surrogate pairs in UTF-16; sanitizing the whole C group would mangle
        // them. They cannot forge a log line, so they are left alone.
        Assert.Equal("movie \U0001F3AC", LogSanitizer.ForLog("movie \U0001F3AC"));
    }
}
