using System.IO;
using Jellyfin.Plugin.SegmentRecognition.Data;
using Jellyfin.Plugin.SegmentRecognition.Providers;
using Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;
using Jellyfin.Plugin.SegmentRecognition.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
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
            var dataPath = Path.Join(applicationPaths.DataPath, "segment-recognition");
            Directory.CreateDirectory(dataPath);
            var dbPath = Path.Join(dataPath, "segments.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                DefaultTimeout = 60,

                // Analysis writes from several workers in parallel while providers read on the
                // playback path. Pooling keeps those connections cheap, and the 60s timeout makes
                // Microsoft.Data.Sqlite retry on SQLITE_BUSY rather than surfacing it immediately.
                // WAL is enabled once on the file itself by DatabaseInitializer.
                Pooling = true,

                // Private, not Shared or Default. Shared-cache mode replaces WAL's reader/writer
                // concurrency with process-wide table-level locks: a reader arriving while any
                // write transaction is open blocks for the full DefaultTimeout above and then
                // fails with SQLITE_LOCKED_SHAREDCACHE, which the busy handler does not retry.
                // Default is not private - it inherits the process-global shared-cache flag, which
                // any other component in the Jellyfin process can turn on. Only Private passes
                // SQLITE_OPEN_PRIVATECACHE and is immune to that.
                Cache = SqliteCacheMode.Private,
            }.ToString();

            options.UseSqlite(connectionString);
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

        serviceCollection.AddSingleton<IScheduledTask, AnalyzeSegmentsTask>();
        serviceCollection.AddSingleton<IScheduledTask, ExportEdlTask>();
    }
}
