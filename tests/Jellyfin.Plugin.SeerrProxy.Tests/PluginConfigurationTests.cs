using System.Xml.Serialization;
using Jellyfin.Plugin.SeerrProxy.Configuration;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void Defaults_FailClosed()
    {
        var configuration = new PluginConfiguration();

        Assert.False(configuration.Enabled);
        Assert.Equal(string.Empty, configuration.SeerrBaseUrl);
        Assert.Equal(string.Empty, configuration.SeerrApiKey);
    }

    [Fact]
    public void XmlRoundTrip_Succeeds()
    {
        // Jellyfin persists plugin configuration with XmlSerializer. A computed property
        // that XmlSerializer cannot handle would throw on every save, so this asserts the
        // whole type stays serializable.
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var original = new PluginConfiguration
        {
            Enabled = true,
            SeerrBaseUrl = "http://jellyseerr:5055",
            SeerrApiKey = "config-key",
            RequestTimeoutSeconds = 45
        };

        using var stream = new MemoryStream();
        serializer.Serialize(stream, original);
        stream.Position = 0;
        var restored = (PluginConfiguration)serializer.Deserialize(stream)!;

        Assert.True(restored.Enabled);
        Assert.Equal("http://jellyseerr:5055", restored.SeerrBaseUrl);
        Assert.Equal("config-key", restored.SeerrApiKey);
        Assert.Equal(45, restored.RequestTimeoutSeconds);
    }

    [Fact]
    public void XmlRoundTrip_DoesNotPersistTheComputedKeySource()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var stream = new MemoryStream();

        serializer.Serialize(stream, new PluginConfiguration());
        var xml = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        Assert.DoesNotContain("ApiKeyFromEnvironment", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(1, 1)]
    [InlineData(45, 45)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(int.MaxValue, 300)]
    public void GetRequestTimeout_ClampsToASafeRange(int configured, int expectedSeconds)
    {
        var configuration = new PluginConfiguration { RequestTimeoutSeconds = configured };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), configuration.GetRequestTimeout());
    }
}
