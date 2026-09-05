using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.SegmentRecognition.Data.Migrations
{
    /// <summary>
    /// Drops the data left behind by the removed intro-skipper import task.
    /// </summary>
    /// <remarks>
    /// The import wrote chapter rows under the <c>intro-skipper import</c> sentinel, which
    /// <c>ChapterNameProvider</c> excludes from both its serve and its cleanup filter - so the rows
    /// were never handed to Jellyfin and never collected. Dropping them here keeps them from
    /// surfacing as segments the moment the sentinel disappears from that exclusion list.
    /// The fingerprints imported alongside them are dropped for the same items: they were produced
    /// by intro-skipper's ffmpeg invocation (stereo, source sample rate) rather than ours (mono,
    /// <c>ChromaprintSampleRate</c>), and were stamped with our own config hash, which would keep
    /// them from ever being regenerated. Fingerprints for items that were fingerprinted but had no
    /// segments to import carry no marker and are left alone; the analysis task rebuilds them once
    /// the configuration changes.
    /// </remarks>
    public partial class RemoveIntroSkipperImportData : Migration
    {
        private const string ImportSentinel = "intro-skipper import";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fingerprints and statuses first, while the chapter rows that identify the imported
            // items still exist.
            migrationBuilder.Sql(
                $"""
                DELETE FROM "ChromaprintResults"
                WHERE "ItemId" IN (
                    SELECT "ItemId" FROM "ChapterAnalysisResults"
                    WHERE "MatchedChapterName" = '{ImportSentinel}')
                """);

            // The import claimed every provider had run. Dropping those rows returns the items to
            // "never analyzed" instead of leaving statuses that describe data no longer present.
            migrationBuilder.Sql(
                $"""
                DELETE FROM "AnalysisStatuses"
                WHERE "ItemId" IN (
                    SELECT "ItemId" FROM "ChapterAnalysisResults"
                    WHERE "MatchedChapterName" = '{ImportSentinel}')
                """);

            migrationBuilder.Sql(
                $"""
                DELETE FROM "ChapterAnalysisResults" WHERE "MatchedChapterName" = '{ImportSentinel}'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted rows cannot be restored; the import task that produced them is gone.
        }
    }
}
