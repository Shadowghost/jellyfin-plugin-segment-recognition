using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Defines the <see cref="PluginServiceRegistrator" />.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<SegmentRecognitionManager>();
        serviceCollection.AddHostedService<AutoSkip>();
    }
}