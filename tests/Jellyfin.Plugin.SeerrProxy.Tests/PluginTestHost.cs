using Jellyfin.Plugin.SeerrProxy.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;

namespace Jellyfin.Plugin.SeerrProxy.Tests;

/// <summary>
/// Stands a <see cref="Plugin"/> instance up around a given configuration.
/// </summary>
/// <remarks>
/// The controller reads configuration through the static <c>Plugin.Instance</c>, so a
/// test that wants a configured plugin has to construct one. Jellyfin loads a plugin's
/// configuration lazily through <see cref="IXmlSerializer"/>, so answering
/// <c>DeserializeFromFile</c> with the desired object installs it without touching disk —
/// the alternative, <c>UpdateConfiguration</c>, would immediately write it back out
/// again.
/// <para>
/// The constructor assigns <c>Plugin.Instance</c>, which is process-wide state. xUnit
/// parallelises across test classes but runs the tests inside one class sequentially, so
/// every test that depends on it must live in a single class.
/// </para>
/// </remarks>
internal static class PluginTestHost
{
    public static Plugin WithConfiguration(PluginConfiguration configuration)
    {
        var applicationPaths = Substitute.For<IApplicationPaths>();
        applicationPaths.PluginConfigurationsPath.Returns(Path.Combine(Path.GetTempPath(), "seerr-proxy-tests"));

        var xmlSerializer = Substitute.For<IXmlSerializer>();
        xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(configuration);

        var plugin = new Plugin(applicationPaths, xmlSerializer);

        // Force the lazy load now, so a later failure surfaces here rather than inside
        // the code under test.
        _ = plugin.Configuration;
        return plugin;
    }
}
