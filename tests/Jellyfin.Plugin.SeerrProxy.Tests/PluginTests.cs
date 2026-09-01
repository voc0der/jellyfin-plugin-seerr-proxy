using Jellyfin.Plugin.SeerrProxy.Configuration;
using Jellyfin.Plugin.SeerrProxy.Models;
using Jellyfin.Plugin.SeerrProxy.Seerr;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

/// <summary>
/// Plugin identity and the small response types.
/// </summary>
/// <remarks>
/// Shares the process-wide <c>Plugin.Instance</c> with
/// <see cref="SeerrProxyControllerTests"/>, so both live in the same xUnit collection to
/// stop them running concurrently.
/// </remarks>
[Collection("PluginInstance")]
public class PluginTests
{
    [Fact]
    public void PluginId_MatchesTheManifestAndConfigurationPage()
    {
        // scripts/verify-plugin-guid.sh enforces this across the repository; this pins the
        // value the assembly itself reports.
        Assert.Equal(Guid.Parse("1ac3cf0f-f0f9-443a-be08-be38e48ff683"), Plugin.PluginId);
    }

    [Fact]
    public void Plugin_ExposesItsIdentity()
    {
        var plugin = PluginTestHost.WithConfiguration(new PluginConfiguration());

        Assert.Equal("Seerr Proxy", plugin.Name);
        Assert.Equal(Plugin.PluginId, plugin.Id);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Description));
    }

    [Fact]
    public void Plugin_ServesTheEmbeddedConfigurationPage()
    {
        var plugin = PluginTestHost.WithConfiguration(new PluginConfiguration());

        var page = Assert.Single(plugin.GetPages());

        Assert.Equal("Seerr Proxy", page.Name);
        Assert.Equal(
            "Jellyfin.Plugin.SeerrProxy.Configuration.configPage.html",
            page.EmbeddedResourcePath);

        // A page whose resource is not actually embedded renders as a blank dashboard tab.
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(stream);
    }

    [Fact]
    public void ErrorResponse_CarriesItsFields()
    {
        var error = new ErrorResponse(404, "UnsupportedProxyEndpoint", "nope");

        Assert.Equal(404, error.StatusCode);
        Assert.Equal("UnsupportedProxyEndpoint", error.Error);
        Assert.Equal("nope", error.Message);
    }

    [Fact]
    public void SeerrStatus_DefaultsToNoVersion()
    {
        Assert.Null(new SeerrStatus().Version);
        Assert.Equal("2.1.0", new SeerrStatus { Version = "2.1.0" }.Version);
    }
}
