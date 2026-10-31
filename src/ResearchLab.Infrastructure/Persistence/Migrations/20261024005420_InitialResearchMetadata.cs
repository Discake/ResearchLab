using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ResearchLab.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialResearchMetadata : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                WorkspacePath = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                GitRemoteUrl = table.Column<string>(type: "TEXT", nullable: true),
                WorkspaceKey = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Projects", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Experiments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                Definition = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Experiments", x => x.Id);
                table.ForeignKey(
                    name: "FK_Experiments_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ExperimentSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                ParametersSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                SourceSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                EnvironmentSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                FinishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                ExperimentId = table.Column<Guid>(type: "TEXT", nullable: false),
                lastTransitionAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Runs", x => x.Id);
                table.ForeignKey(
                    name: "FK_Runs_Experiments_ExperimentId",
                    column: x => x.ExperimentId,
                    principalTable: "Experiments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "MetricPoints",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<double>(type: "REAL", nullable: false),
                Step = table.Column<long>(type: "INTEGER", nullable: true),
                RecordedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Unit = table.Column<string>(type: "TEXT", nullable: true),
                Sequence = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MetricPoints", x => x.Id);
                table.ForeignKey(
                    name: "FK_MetricPoints_Runs_RunId",
                    column: x => x.RunId,
                    principalTable: "Runs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Experiments_ProjectId",
            table: "Experiments",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_MetricPoints_RunId_Sequence",
            table: "MetricPoints",
            columns: new[] { "RunId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Projects_WorkspaceKey",
            table: "Projects",
            column: "WorkspaceKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Runs_ExperimentId",
            table: "Runs",
            column: "ExperimentId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MetricPoints");

        migrationBuilder.DropTable(
            name: "Runs");

        migrationBuilder.DropTable(
            name: "Experiments");

        migrationBuilder.DropTable(
            name: "Projects");
    }
}
