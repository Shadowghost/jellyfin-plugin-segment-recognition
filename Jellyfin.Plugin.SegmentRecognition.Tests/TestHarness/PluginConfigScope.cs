using System;
using System.IO;
using System.Reflection;
using Jellyfin.Plugin.SegmentRecognition.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;

/// <summary>
/// Installs a <see cref="Plugin"/> singleton with a caller-supplied configuration for the
/// duration of a test, restoring the previous instance on dispose.
/// </summary>
/// <remarks>
/// Providers read their settings from the <c>Plugin.Instance.Configuration</c> static rather than
/// through injection, so exercising them at all requires the singleton to exist. Tests that touch
/// it must not run in parallel with each other - see <c>PluginStateCollection</c>.
/// </remarks>
public sealed class PluginConfigScope : IDisposable
{
    private static readonly PropertyInfo _instanceProperty =
        typeof(Plugin).GetProperty(nameof(Plugin.Instance), BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("Plugin.Instance property not found");

    private readonly Plugin? _previous;
    private readonly string _tempDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigScope"/> class.
    /// </summary>
    /// <param name="configure">Optional mutation applied to the fresh default configuration.</param>
    public PluginConfigScope(Action<PluginConfiguration>? configure = null)
    {
        _previous = Plugin.Instance;

        _tempDir = Path.Join(Path.GetTempPath(), "segrec-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.ConfigurationDirectoryPath.Returns(_tempDir);

        var serializer = Substitute.For<IXmlSerializer>();

        var plugin = new Plugin(paths, serializer);
        _instanceProperty.SetValue(null, plugin);

        Configuration = new PluginConfiguration();
        configure?.Invoke(Configuration);
        plugin.UpdateConfiguration(Configuration);
    }

    /// <summary>
    /// Gets the configuration installed for this scope.
    /// </summary>
    public PluginConfiguration Configuration { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _instanceProperty.SetValue(null, _previous);

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leaked temp dir must never fail a test.
        }
    }
}
