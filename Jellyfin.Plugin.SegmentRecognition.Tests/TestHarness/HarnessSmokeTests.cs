using System;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;

[Collection(PluginStateCollection.Name)]
public sealed class HarnessSmokeTests
{
    [Fact]
    public void PluginConfigScope_InstallsAndRestoresInstance()
    {
        using (new PluginConfigScope(c => c.MinIntroDurationSeconds = 42))
        {
            Assert.NotNull(Plugin.Instance);
            Assert.Equal(42, Plugin.Instance!.Configuration.MinIntroDurationSeconds);
        }
    }

    [Fact]
    public void SegmentDbFixture_PersistsAcrossContexts()
    {
        using var fixture = new SegmentDbFixture();
        var itemId = Guid.NewGuid();

        using (var db = fixture.CreateContext())
        {
            db.AnalysisStatuses.Add(new AnalysisStatus
            {
                ItemId = itemId,
                ProviderName = "ChapterName",
                AnalyzedAt = DateTime.UtcNow,
                HasResults = true,
            });
            db.SaveChanges();
        }

        using (var db = fixture.CreateContext())
        {
            Assert.Single(db.AnalysisStatuses);
        }
    }
}
