using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.SegmentRecognition.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalysisStatuses",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HasResults = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisStatuses", x => new { x.ItemId, x.ProviderName });
                });

            migrationBuilder.CreateTable(
                name: "BlackFrameResults",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    BlackPercentage = table.Column<double>(type: "REAL", nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlackFrameResults", x => new { x.ItemId, x.TimestampTicks });
                });

            migrationBuilder.CreateTable(
                name: "ChapterAnalysisResults",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SegmentType = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchedChapterName = table.Column<string>(type: "TEXT", nullable: false),
                    StartTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterAnalysisResults", x => new { x.ItemId, x.SegmentType, x.MatchedChapterName });
                });

            migrationBuilder.CreateTable(
                name: "ChromaprintResults",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    SeasonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FingerprintData = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AnalysisDurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChromaprintResults", x => new { x.ItemId, x.Region });
                });

            migrationBuilder.CreateTable(
                name: "CropDetectResults",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CropWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    CropHeight = table.Column<int>(type: "INTEGER", nullable: false),
                    CropX = table.Column<int>(type: "INTEGER", nullable: false),
                    CropY = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CropDetectResults", x => x.ItemId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisStatuses_ItemId",
                table: "AnalysisStatuses",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_BlackFrameResults_ItemId",
                table: "BlackFrameResults",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterAnalysisResults_ItemId",
                table: "ChapterAnalysisResults",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ChromaprintResults_SeasonId",
                table: "ChromaprintResults",
                column: "SeasonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisStatuses");

            migrationBuilder.DropTable(
                name: "BlackFrameResults");

            migrationBuilder.DropTable(
                name: "ChapterAnalysisResults");

            migrationBuilder.DropTable(
                name: "ChromaprintResults");

            migrationBuilder.DropTable(
                name: "CropDetectResults");
        }
    }
}
