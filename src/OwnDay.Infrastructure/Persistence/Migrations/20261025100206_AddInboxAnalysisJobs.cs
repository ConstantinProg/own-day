using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddInboxAnalysisJobs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "inbox_analysis_jobs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                inbox_item_id = table.Column<long>(type: "bigint", nullable: false),
                user_id = table.Column<long>(type: "bigint", nullable: false),
                analysis_id = table.Column<Guid>(type: "uuid", nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                lease_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                last_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox_analysis_jobs", x => x.id);
                table.CheckConstraint("CK_inbox_analysis_jobs_attempts", "attempt_count >= 0");
                table.CheckConstraint("CK_inbox_analysis_jobs_lifecycle", "(status = 1 AND lease_token IS NOT NULL AND lease_until IS NOT NULL) OR (status IN (0, 2, 3, 4) AND lease_token IS NULL AND lease_until IS NULL)");
                table.ForeignKey(
                    name: "FK_inbox_analysis_jobs_inbox_analyses_analysis_id",
                    column: x => x.analysis_id,
                    principalTable: "inbox_analyses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_inbox_analysis_jobs_inbox_items_inbox_item_id_user_id",
                    columns: x => new { x.inbox_item_id, x.user_id },
                    principalTable: "inbox_items",
                    principalColumns: new[] { "id", "user_id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_inbox_analysis_jobs_analysis_id",
            table: "inbox_analysis_jobs",
            column: "analysis_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_inbox_analysis_jobs_available_at_id",
            table: "inbox_analysis_jobs",
            columns: new[] { "available_at", "id" },
            filter: "status IN (0, 1)");

        migrationBuilder.CreateIndex(
            name: "IX_inbox_analysis_jobs_inbox_item_id",
            table: "inbox_analysis_jobs",
            column: "inbox_item_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_inbox_analysis_jobs_inbox_item_id_user_id",
            table: "inbox_analysis_jobs",
            columns: new[] { "inbox_item_id", "user_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "inbox_analysis_jobs");
    }
}
