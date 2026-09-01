using Jellyfin.Plugin.SeerrProxy.Seerr;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class SeerrUserTests
{
    [Fact]
    public void GetSafeDisplayName_PrefersDisplayName()
    {
        var user = new SeerrUser
        {
            DisplayName = "Ada",
            Username = "ada",
            JellyfinUsername = "ada-jf",
            PlexUsername = "ada-plex"
        };

        Assert.Equal("Ada", user.GetSafeDisplayName());
    }

    [Fact]
    public void GetSafeDisplayName_FallsBackInOrder()
    {
        Assert.Equal("ada", new SeerrUser { Username = "ada", JellyfinUsername = "ada-jf" }.GetSafeDisplayName());
        Assert.Equal("ada-jf", new SeerrUser { JellyfinUsername = "ada-jf", PlexUsername = "ada-plex" }.GetSafeDisplayName());
        Assert.Equal("ada-plex", new SeerrUser { PlexUsername = "ada-plex" }.GetSafeDisplayName());
    }

    [Fact]
    public void GetSafeDisplayName_SkipsBlankCandidates()
    {
        var user = new SeerrUser
        {
            DisplayName = string.Empty,
            Username = "   ",
            JellyfinUsername = "ada-jf"
        };

        Assert.Equal("ada-jf", user.GetSafeDisplayName());
    }

    [Fact]
    public void GetSafeDisplayName_NothingUsable_IsNull()
    {
        Assert.Null(new SeerrUser().GetSafeDisplayName());
        Assert.Null(new SeerrUser { DisplayName = " ", Username = string.Empty }.GetSafeDisplayName());
    }
}
