using Jellyfin.Plugin.SeerrProxy.Security;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class ApiAllowlistTests
{
    [Theory]
    [InlineData("GET", "auth/me")]
    [InlineData("GET", "settings/public")]
    [InlineData("GET", "search")]
    [InlineData("GET", "discover/movies")]
    [InlineData("GET", "discover/tv/upcoming")]
    [InlineData("GET", "movie/550")]
    [InlineData("GET", "movie/550/recommendations")]
    [InlineData("GET", "movie/550/similar")]
    [InlineData("GET", "movie/550/ratings")]
    [InlineData("GET", "movie/550/ratingscombined")]
    [InlineData("GET", "tv/1396")]
    [InlineData("GET", "tv/1396/season/2")]
    [InlineData("GET", "person/287")]
    [InlineData("GET", "person/287/combined_credits")]
    [InlineData("GET", "request")]
    [InlineData("GET", "request/42")]
    [InlineData("POST", "request")]
    [InlineData("PUT", "request/42")]
    [InlineData("DELETE", "request/42")]
    public void IsAllowed_DocumentedRoutes_AreAllowed(string method, string path)
    {
        Assert.True(ApiAllowlist.IsAllowed(method, path));
    }

    [Theory]
    [InlineData("GET", "user")]
    [InlineData("GET", "user/1")]
    [InlineData("GET", "settings/main")]
    [InlineData("GET", "settings/jellyfin")]
    [InlineData("GET", "auth/login")]
    [InlineData("GET", "")]
    [InlineData("GET", "/")]
    [InlineData("POST", "user")]
    [InlineData("POST", "request/42")]
    [InlineData("PUT", "request")]
    [InlineData("DELETE", "request")]
    [InlineData("PATCH", "request/42")]
    [InlineData("HEAD", "search")]
    public void IsAllowed_EverythingElse_IsRefused(string method, string path)
    {
        Assert.False(ApiAllowlist.IsAllowed(method, path));
    }

    [Theory]
    [InlineData("GET", "discover/../user")]
    [InlineData("GET", "discover/../../user/1")]
    [InlineData("GET", "discover/./movies")]
    [InlineData("GET", "discover/...")]
    [InlineData("GET", "movie/550/../../user")]
    [InlineData("GET", "request/..")]
    public void IsAllowed_DotSegments_AreRefused(string method, string path)
    {
        Assert.False(ApiAllowlist.IsAllowed(method, path));
    }

    [Theory]
    [InlineData("GET", "discover/movies%2f..%2fuser")]
    [InlineData("GET", "discover/mov ies")]
    [InlineData("GET", "discover/movies?x=1")]
    [InlineData("GET", "discover/movies#x")]
    [InlineData("GET", "discover/mov\nies")]
    [InlineData("GET", "discover/mov@ies")]
    public void IsAllowed_UnsafeSegmentCharacters_AreRefused(string method, string path)
    {
        Assert.False(ApiAllowlist.IsAllowed(method, path));
    }

    [Fact]
    public void IsAllowed_TooManySegments_IsRefused()
    {
        var deep = "discover/" + string.Join('/', Enumerable.Repeat("a", 16));

        Assert.False(ApiAllowlist.IsAllowed("GET", deep));
    }

    [Fact]
    public void IsAllowed_OverlongSegment_IsRefused()
    {
        Assert.False(ApiAllowlist.IsAllowed("GET", "discover/" + new string('a', 65)));
    }

    [Theory]
    [InlineData("GET", "movie/0")]
    [InlineData("GET", "movie/-1")]
    [InlineData("GET", "movie/1.5")]
    [InlineData("GET", "movie/abc")]
    [InlineData("GET", "tv/1396/season/0")]
    [InlineData("DELETE", "request/0")]
    public void IsAllowed_NonPositiveIdentifiers_AreRefused(string method, string path)
    {
        Assert.False(ApiAllowlist.IsAllowed(method, path));
    }

    [Fact]
    public void IsAllowed_MethodAndPathAreCheckedTogether()
    {
        // "request" is a legitimate GET and POST target but not a PUT or DELETE one.
        Assert.True(ApiAllowlist.IsAllowed("GET", "request"));
        Assert.True(ApiAllowlist.IsAllowed("POST", "request"));
        Assert.False(ApiAllowlist.IsAllowed("PUT", "request"));
        Assert.False(ApiAllowlist.IsAllowed("DELETE", "request"));
    }

    [Fact]
    public void IsAllowed_RouteFamiliesAreCaseInsensitive()
    {
        Assert.True(ApiAllowlist.IsAllowed("GET", "Auth/Me"));
        Assert.True(ApiAllowlist.IsAllowed("GET", "MOVIE/550"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?query=dune")]
    [InlineData("?page=2&sortBy=added")]
    public void IsAllowedQuery_OrdinaryQueries_AreAllowed(string? query)
    {
        Assert.True(ApiAllowlist.IsAllowedQuery(query));
    }

    [Theory]
    [InlineData("?a=1#b")]
    [InlineData("?a=\n")]
    [InlineData("?a=\r\n")]
    [InlineData("?a=\0")]
    public void IsAllowedQuery_FragmentsAndControlCharacters_AreRefused(string query)
    {
        Assert.False(ApiAllowlist.IsAllowedQuery(query));
    }

    [Fact]
    public void IsAllowedQuery_OverlongQuery_IsRefused()
    {
        Assert.False(ApiAllowlist.IsAllowedQuery("?q=" + new string('a', ApiAllowlist.MaxQueryLength)));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/request/", "request")]
    [InlineData("request", "request")]
    [InlineData("//request//", "request")]
    public void NormalizePath_TrimsSlashes(string? path, string expected)
    {
        Assert.Equal(expected, ApiAllowlist.NormalizePath(path));
    }

    [Theory]
    [InlineData("movie", true)]
    [InlineData("combined_credits", true)]
    [InlineData("some.thing", true)]
    [InlineData("a-b~c", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("....", false)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("a%2fb", false)]
    public void IsSafeSegment_ChecksShapeAndDotSegments(string segment, bool expected)
    {
        Assert.Equal(expected, ApiAllowlist.IsSafeSegment(segment));
    }
}
