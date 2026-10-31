using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ResearchLab.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class MetricSeriesLookup : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_MetricPoints_RunId_Name_Sequence",
            table: "MetricPoints",
            columns: new[] { "RunId", "Name", "Sequence" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MetricPoints_RunId_Name_Sequence",
            table: "MetricPoints");
    }
}
