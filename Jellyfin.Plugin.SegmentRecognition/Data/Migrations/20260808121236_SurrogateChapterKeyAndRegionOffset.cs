using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.SegmentRecognition.Data.Migrations
{
    /// <inheritdoc />
    public partial class SurrogateChapterKeyAndRegionOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ChapterAnalysisResults",
                table: "ChapterAnalysisResults");

            migrationBuilder.AddColumn<long>(
                name: "RegionStartTicks",
                table: "ChromaprintResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "ChapterAnalysisResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ChapterAnalysisResults",
                table: "ChapterAnalysisResults",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterAnalysisResults_ItemId_SegmentType_MatchedChapterName_StartTicks",
                table: "ChapterAnalysisResults",
                columns: new[] { "ItemId", "SegmentType", "MatchedChapterName", "StartTicks" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ChapterAnalysisResults",
                table: "ChapterAnalysisResults");

            migrationBuilder.DropIndex(
                name: "IX_ChapterAnalysisResults_ItemId_SegmentType_MatchedChapterName_StartTicks",
                table: "ChapterAnalysisResults");

            migrationBuilder.DropColumn(
                name: "RegionStartTicks",
                table: "ChromaprintResults");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "ChapterAnalysisResults");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ChapterAnalysisResults",
                table: "ChapterAnalysisResults",
                columns: new[] { "ItemId", "SegmentType", "MatchedChapterName" });
        }
    }
}
