using Jellyfin.Plugin.SeerrProxy.Configuration;
using Jellyfin.Plugin.SeerrProxy.Security;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

public class SeerrProxySecretSourceTests
{
    private static SeerrProxySecretSource Create(
        IDictionary<string, string?> environment,
        IDictionary<string, string>? files = null)
    {
        return new SeerrProxySecretSource(
            name => environment.TryGetValue(name, out var value) ? value : null,
            path => files is not null && files.TryGetValue(path, out var contents)
                ? contents
                : throw new FileNotFoundException(path));
    }

    [Fact]
    public void GetAdminSecretHash_FromVariable_IsReturned()
    {
        var source = Create(new Dictionary<string, string?>
        {
            [SeerrProxySecretSource.AdminSecretHashVariable] = "  deadbeef  "
        });

        Assert.Equal("deadbeef", source.GetAdminSecretHash());
    }

    [Fact]
    public void GetAdminSecretHash_FileTakesPrecedenceOverVariable()
    {
        var source = Create(
            new Dictionary<string, string?>
            {
                [SeerrProxySecretSource.AdminSecretHashVariable] = "from-variable",
                [SeerrProxySecretSource.AdminSecretHashFileVariable] = "/run/secrets/hash"
            },
            new Dictionary<string, string> { ["/run/secrets/hash"] = "from-file\n" });

        Assert.Equal("from-file", source.GetAdminSecretHash());
    }

    [Fact]
    public void GetAdminSecretHash_UnreadableFile_FailsClosed()
    {
        // Deliberately does not fall through to the variable: a file that was configured
        // and cannot be read is an error state, not an invitation to use a weaker source.
        var source = Create(new Dictionary<string, string?>
        {
            [SeerrProxySecretSource.AdminSecretHashVariable] = "from-variable",
            [SeerrProxySecretSource.AdminSecretHashFileVariable] = "/run/secrets/missing"
        });

        Assert.Null(source.GetAdminSecretHash());
    }

    [Fact]
    public void GetAdminSecretHash_BlankFile_IsNull()
    {
        var source = Create(
            new Dictionary<string, string?>
            {
                [SeerrProxySecretSource.AdminSecretHashFileVariable] = "/run/secrets/hash"
            },
            new Dictionary<string, string> { ["/run/secrets/hash"] = "   \n" });

        Assert.Null(source.GetAdminSecretHash());
    }

    [Fact]
    public void GetAdminSecretHash_NothingConfigured_IsNull()
    {
        Assert.Null(Create(new Dictionary<string, string?>()).GetAdminSecretHash());
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("maybe", false)]
    public void IsAdminSecretRequired_ReadsTruthyValues(string? value, bool expected)
    {
        var source = Create(new Dictionary<string, string?>
        {
            [SeerrProxySecretSource.RequireAdminSecretVariable] = value
        });

        Assert.Equal(expected, source.IsAdminSecretRequired());
    }

    [Fact]
    public void ResolveApiKey_EnvironmentBeatsConfiguration()
    {
        var source = Create(new Dictionary<string, string?>
        {
            [SeerrProxySecretSource.ApiKeyVariable] = "env-key"
        });

        var configuration = new PluginConfiguration { SeerrApiKey = "config-key" };

        Assert.Equal("env-key", source.ResolveApiKey(configuration));
    }

    [Fact]
    public void ResolveApiKey_FileBeatsVariable()
    {
        var source = Create(
            new Dictionary<string, string?>
            {
                [SeerrProxySecretSource.ApiKeyVariable] = "env-key",
                [SeerrProxySecretSource.ApiKeyFileVariable] = "/run/secrets/api-key"
            },
            new Dictionary<string, string> { ["/run/secrets/api-key"] = "file-key\n" });

        Assert.Equal("file-key", source.ResolveApiKey(new PluginConfiguration()));
    }

    [Fact]
    public void ResolveApiKey_FallsBackToConfiguration()
    {
        var source = Create(new Dictionary<string, string?>());
        var configuration = new PluginConfiguration { SeerrApiKey = "  config-key  " };

        Assert.Equal("config-key", source.ResolveApiKey(configuration));
    }

    [Fact]
    public void ResolveApiKey_NothingAnywhere_IsNull()
    {
        var source = Create(new Dictionary<string, string?>());

        Assert.Null(source.ResolveApiKey(new PluginConfiguration()));
    }

    [Fact]
    public void IsConfigured_RequiresBaseUrlAndKey()
    {
        var withEnvironmentKey = Create(new Dictionary<string, string?>
        {
            [SeerrProxySecretSource.ApiKeyVariable] = "env-key"
        });
        var withoutKey = Create(new Dictionary<string, string?>());

        Assert.False(withoutKey.IsConfigured(new PluginConfiguration { SeerrBaseUrl = "http://seerr:5055" }));
        Assert.False(withEnvironmentKey.IsConfigured(new PluginConfiguration()));
        Assert.True(withEnvironmentKey.IsConfigured(new PluginConfiguration { SeerrBaseUrl = "http://seerr:5055" }));
    }

    [Fact]
    public void Variables_AreNotJellyfinPrefixed()
    {
        // Jellyfin logs every JELLYFIN_/DOTNET_/ASPNETCORE_ variable, with its value, at
        // every startup. A prefixed name would print these into the server log on boot.
        string[] names =
        [
            SeerrProxySecretSource.AdminSecretHashVariable,
            SeerrProxySecretSource.AdminSecretHashFileVariable,
            SeerrProxySecretSource.RequireAdminSecretVariable,
            SeerrProxySecretSource.ApiKeyVariable,
            SeerrProxySecretSource.ApiKeyFileVariable
        ];

        Assert.All(names, name =>
        {
            Assert.DoesNotContain("JELLYFIN_", name, StringComparison.Ordinal);
            Assert.DoesNotContain("DOTNET_", name, StringComparison.Ordinal);
            Assert.DoesNotContain("ASPNETCORE_", name, StringComparison.Ordinal);
        });
    }
}
