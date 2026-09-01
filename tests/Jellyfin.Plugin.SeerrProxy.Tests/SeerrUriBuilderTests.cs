using Jellyfin.Plugin.SeerrProxy.Configuration;
using Jellyfin.Plugin.SeerrProxy.Seerr;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class SeerrUriBuilderTests
{
    private static PluginConfiguration Config(string baseUrl)
    {
        return new PluginConfiguration { SeerrBaseUrl = baseUrl };
    }

    [Theory]
    [InlineData("http://jellyseerr:5055", "http://jellyseerr:5055/api/v1/")]
    [InlineData("http://jellyseerr:5055/", "http://jellyseerr:5055/api/v1/")]
    [InlineData("https://requests.example.com", "https://requests.example.com/api/v1/")]
    [InlineData("https://requests.example.com/", "https://requests.example.com/api/v1/")]
    [InlineData("  https://requests.example.com  ", "https://requests.example.com/api/v1/")]
    [InlineData("https://example.com/seerr", "https://example.com/seerr/api/v1/")]
    [InlineData("https://example.com/seerr/", "https://example.com/seerr/api/v1/")]
    public void BuildApiRoot_AppendsApiV1(string baseUrl, string expected)
    {
        Assert.Equal(expected, SeerrUriBuilder.BuildApiRoot(Config(baseUrl)).ToString());
    }

    [Theory]
    [InlineData("http://jellyseerr:5055/api/v1")]
    [InlineData("http://jellyseerr:5055/api/v1/")]
    public void BuildApiRoot_DoesNotDoubleUpApiV1(string baseUrl)
    {
        Assert.Equal("http://jellyseerr:5055/api/v1/", SeerrUriBuilder.BuildApiRoot(Config(baseUrl)).ToString());
    }

    [Fact]
    public void BuildApiRoot_DropsQueryAndFragment()
    {
        var root = SeerrUriBuilder.BuildApiRoot(Config("https://example.com/seerr?x=1#y"));

        Assert.Equal("https://example.com/seerr/api/v1/", root.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildApiRoot_MissingBaseUrl_Throws(string baseUrl)
    {
        Assert.Throws<SeerrConfigurationException>(() => SeerrUriBuilder.BuildApiRoot(Config(baseUrl)));
    }

    [Theory]
    [InlineData("jellyseerr:5055")]
    [InlineData("ftp://jellyseerr:5055")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/only")]
    public void BuildApiRoot_NonHttpBaseUrl_Throws(string baseUrl)
    {
        Assert.Throws<SeerrConfigurationException>(() => SeerrUriBuilder.BuildApiRoot(Config(baseUrl)));
    }

    [Fact]
    public void BuildApiRoot_EmbeddedCredentials_Throws()
    {
        Assert.Throws<SeerrConfigurationException>(
            () => SeerrUriBuilder.BuildApiRoot(Config("https://user:pass@example.com")));
    }

    [Theory]
    [InlineData("status", "http://jellyseerr:5055/api/v1/status")]
    [InlineData("/status", "http://jellyseerr:5055/api/v1/status")]
    [InlineData("auth/me", "http://jellyseerr:5055/api/v1/auth/me")]
    [InlineData("search?query=dune", "http://jellyseerr:5055/api/v1/search?query=dune")]
    [InlineData("discover/movies?page=2", "http://jellyseerr:5055/api/v1/discover/movies?page=2")]
    [InlineData("tv/1396/season/2", "http://jellyseerr:5055/api/v1/tv/1396/season/2")]
    public void Build_ComposesUnderTheApiRoot(string relativePath, string expected)
    {
        Assert.Equal(expected, SeerrUriBuilder.Build(Config("http://jellyseerr:5055"), relativePath).ToString());
    }

    [Fact]
    public void Build_UnderASubPathBase_StaysUnderIt()
    {
        var uri = SeerrUriBuilder.Build(Config("https://example.com/seerr"), "request/42");

        Assert.Equal("https://example.com/seerr/api/v1/request/42", uri.ToString());
    }

    [Theory]
    [InlineData("../user/1")]
    [InlineData("discover/../../user/1")]
    [InlineData("../../../../etc/passwd")]
    [InlineData("..")]
    [InlineData("../")]
    public void Build_PathEscapingTheApiRoot_Throws(string relativePath)
    {
        // ApiAllowlist rejects these first. This is the independent second check, so it
        // must hold on its own even if the allowlist is ever loosened.
        Assert.Throws<SeerrConfigurationException>(
            () => SeerrUriBuilder.Build(Config("http://jellyseerr:5055"), relativePath));
    }

    [Fact]
    public void Build_AbsoluteUrlAsRelativePath_Throws()
    {
        // Uri relative resolution would otherwise honour an absolute URL wholesale and
        // send the API key to an attacker-named host.
        Assert.Throws<SeerrConfigurationException>(
            () => SeerrUriBuilder.Build(Config("http://jellyseerr:5055"), "http://evil.example.com/steal"));
    }

    [Fact]
    public void Build_ProtocolRelativeUrlAsRelativePath_StaysOnTheConfiguredHost()
    {
        // Leading slashes are trimmed before resolution, so "//host/path" is treated as
        // a path segment rather than as an authority. Were that trim ever removed, the
        // containment check would reject the result instead — the two are independent.
        var uri = SeerrUriBuilder.Build(Config("http://jellyseerr:5055"), "//evil.example.com/steal");

        Assert.Equal("jellyseerr", uri.Host);
        Assert.Equal("http://jellyseerr:5055/api/v1/evil.example.com/steal", uri.ToString());
    }

    [Fact]
    public void Build_QueryIsNotPartOfTheContainmentCheck()
    {
        // A query may legitimately contain anything once encoded; containment is about
        // scheme, host, port, and path only.
        var uri = SeerrUriBuilder.Build(Config("http://jellyseerr:5055"), "search?query=%2F..%2F..%2Fetc");

        Assert.StartsWith("http://jellyseerr:5055/api/v1/search?", uri.ToString(), StringComparison.Ordinal);
    }
}
