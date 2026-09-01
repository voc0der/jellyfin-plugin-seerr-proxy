using System.Text.Json.Nodes;
using Jellyfin.Plugin.SeerrProxy.Security;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class ForwardedPayloadTests
{
    [Fact]
    public void StripIdentity_RemovesUserId()
    {
        var payload = JsonNode.Parse("""{"mediaType":"movie","mediaId":550,"userId":3}""");

        var result = ForwardedPayload.StripIdentity(payload, out var removed);

        Assert.Equal("userId", Assert.Single(removed));
        Assert.Null(result!["userId"]);
        Assert.Equal("movie", result["mediaType"]!.GetValue<string>());
        Assert.Equal(550, result["mediaId"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("userId")]
    [InlineData("user")]
    [InlineData("requestedBy")]
    [InlineData("modifiedBy")]
    public void StripIdentity_RemovesEveryIdentityProperty(string property)
    {
        var payload = new JsonObject
        {
            ["mediaType"] = "tv",
            [property] = 7
        };

        var result = ForwardedPayload.StripIdentity(payload, out var removed);

        Assert.Equal(property, Assert.Single(removed));
        Assert.Null(result!.AsObject()[property]);
    }

    [Theory]
    [InlineData("USERID")]
    [InlineData("UserId")]
    [InlineData("userid")]
    public void StripIdentity_IsCaseInsensitive(string property)
    {
        var payload = new JsonObject
        {
            ["mediaType"] = "movie",
            [property] = 3
        };

        ForwardedPayload.StripIdentity(payload, out var removed);

        Assert.Equal(property, Assert.Single(removed));
        Assert.False(payload.ContainsKey(property));
    }

    [Fact]
    public void StripIdentity_RemovesSeveralAtOnce()
    {
        var payload = JsonNode.Parse(
            """{"mediaType":"movie","userId":3,"requestedBy":{"id":9},"is4k":true}""");

        var result = ForwardedPayload.StripIdentity(payload, out var removed);

        Assert.Equal(2, removed.Count);
        Assert.Contains("userId", removed);
        Assert.Contains("requestedBy", removed);
        Assert.True(result!["is4k"]!.GetValue<bool>());
    }

    [Fact]
    public void StripIdentity_LeavesLegitimatePayloadUntouched()
    {
        var payload = JsonNode.Parse(
            """{"mediaType":"tv","mediaId":1396,"seasons":[1,2],"is4k":false,"serverId":0}""");

        var result = ForwardedPayload.StripIdentity(payload, out var removed);

        Assert.Empty(removed);
        Assert.Equal(
            """{"mediaType":"tv","mediaId":1396,"seasons":[1,2],"is4k":false,"serverId":0}""",
            result!.ToJsonString());
    }

    [Fact]
    public void StripIdentity_NullPayload_IsPassedThrough()
    {
        Assert.Null(ForwardedPayload.StripIdentity(null, out var removed));
        Assert.Empty(removed);
    }

    [Fact]
    public void StripIdentity_NonObjectPayload_IsPassedThrough()
    {
        // Seerr would reject these anyway; the point is that stripping does not throw.
        var array = JsonNode.Parse("[1,2,3]");

        var result = ForwardedPayload.StripIdentity(array, out var removed);

        Assert.Empty(removed);
        Assert.Equal("[1,2,3]", result!.ToJsonString());
    }

    [Fact]
    public void StripIdentity_NestedUserId_IsLeftAlone()
    {
        // Only top-level identity is honoured by Seerr's request API, and rewriting
        // arbitrary nested data would corrupt legitimate payloads.
        var payload = JsonNode.Parse("""{"mediaType":"movie","meta":{"userId":3}}""");

        var result = ForwardedPayload.StripIdentity(payload, out var removed);

        Assert.Empty(removed);
        Assert.Equal(3, result!["meta"]!["userId"]!.GetValue<int>());
    }
}
