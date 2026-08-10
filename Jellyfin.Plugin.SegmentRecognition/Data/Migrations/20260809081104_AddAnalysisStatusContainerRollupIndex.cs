using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.SegmentRecognition.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisStatusContainerRollupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnalysisStatuses_ContainerId",
                table: "AnalysisStatuses");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisStatuses_ContainerRollup",
                table: "AnalysisStatuses",
                columns: new[] { "ContainerId", "HasResults", "AnalyzedAt", "ProviderName", "LastError" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnalysisStatuses_ContainerRollup",
                table: "AnalysisStatuses");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisStatuses_ContainerId",
                table: "AnalysisStatuses",
                column: "ContainerId");
        }
    }
}
