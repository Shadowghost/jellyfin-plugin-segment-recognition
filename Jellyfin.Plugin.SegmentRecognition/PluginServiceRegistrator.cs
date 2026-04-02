using System.IO;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.EventHandlers;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Registers the plugin's services with the DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // IApplicationPaths is not resolvable during RegisterServices (DI container not yet built).
        // Use a factory that resolves it at activation time.
        serviceCollection.AddDbContextFactory<SegmentDbContext>((sp, options) =>
        {
            var applicationPaths = sp.GetRequiredService<IApplicationPaths>();
            var dataPath = Path.Combine(applicationPaths.DataPath, "segment-recognition");
            Directory.CreateDirectory(dataPath);
            var dbPath = Path.Combine(dataPath, "segments.db");
            options.UseSqlite($"Data Source={dbPath}");
        });

        serviceCollection.AddHostedService<DatabaseInitializer>();

        serviceCollection.AddSingleton<FfmpegBlackFrameService>();
        serviceCollection.AddSingleton<FfmpegChromaprintService>();
        serviceCollection.AddSingleton<SegmentRefiner>();
        serviceCollection.AddSingleton<ChapterSnapper>();
        serviceCollection.AddSingleton<KeyframeSnapper>();
        serviceCollection.AddSingleton<RefinementPipeline>();

        serviceCollection.AddSingleton<ChapterNameProvider>();
        serviceCollection.AddSingleton<IMediaSegmentProvider>(sp => sp.GetRequiredService<ChapterNameProvider>());
        serviceCollection.AddSingleton<BlackFrameProvider>();
        serviceCollection.AddSingleton<IMediaSegmentProvider>(sp => sp.GetRequiredService<BlackFrameProvider>());
        serviceCollection.AddSingleton<ChromaprintProvider>();
        serviceCollection.AddSingleton<IMediaSegmentProvider>(sp => sp.GetRequiredService<ChromaprintProvider>());
        serviceCollection.AddSingleton<EdlImportProvider>();
        serviceCollection.AddSingleton<IMediaSegmentProvider>(sp => sp.GetRequiredService<EdlImportProvider>());

        serviceCollection.AddHostedService<LibraryItemRemovedNotifier>();
        serviceCollection.AddSingleton<IScheduledTask, ImportIntroSkipperDataTask>();
        serviceCollection.AddSingleton<IScheduledTask, AnalyzeSegmentsTask>();
        serviceCollection.AddSingleton<IScheduledTask, ExportEdlTask>();
    }
}
