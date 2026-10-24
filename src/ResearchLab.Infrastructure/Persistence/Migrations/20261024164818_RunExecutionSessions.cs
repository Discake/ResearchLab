using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ResearchLab.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class RunExecutionSessions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "OwnerSessionId",
            table: "Runs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Runs_OwnerSessionId_Status",
            table: "Runs",
            columns: new[] { "OwnerSessionId", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Runs_OwnerSessionId_Status",
            table: "Runs");

        migrationBuilder.DropColumn(
            name: "OwnerSessionId",
            table: "Runs");
    }
}
