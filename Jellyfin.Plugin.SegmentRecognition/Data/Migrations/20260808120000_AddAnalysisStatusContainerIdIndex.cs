using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.SegmentRecognition.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisStatusContainerIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AnalysisStatuses_ContainerId",
                table: "AnalysisStatuses",
                column: "ContainerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnalysisStatuses_ContainerId",
                table: "AnalysisStatuses");
        }
    }
}
