using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddInboxAnalysis : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddUniqueConstraint(
            name: "AK_inbox_items_id_user_id",
            table: "inbox_items",
            columns: new[] { "id", "user_id" });

        migrationBuilder.CreateTable(
            name: "inbox_analyses",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                inbox_item_id = table.Column<long>(type: "bigint", nullable: false),
                user_id = table.Column<long>(type: "bigint", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                schema_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                context = table.Column<string>(type: "jsonb", nullable: false),
                context_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                input_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                diagnostics = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox_analyses", x => x.id);
                table.CheckConstraint("CK_inbox_analyses_lifecycle", "(status = 0 AND completed_at IS NULL) OR (status BETWEEN 1 AND 3 AND completed_at IS NOT NULL AND completed_at >= created_at)");
                table.CheckConstraint("CK_inbox_analyses_version", "version > 0");
                table.ForeignKey(
                    name: "FK_inbox_analyses_inbox_items_inbox_item_id_user_id",
                    columns: x => new { x.inbox_item_id, x.user_id },
                    principalTable: "inbox_items",
                    principalColumns: new[] { "id", "user_id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "inbox_analysis_proposals",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                analysis_id = table.Column<Guid>(type: "uuid", nullable: false),
                position = table.Column<int>(type: "integer", nullable: false),
                kind = table.Column<int>(type: "integer", nullable: false),
                title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox_analysis_proposals", x => x.id);
                table.CheckConstraint("CK_inbox_analysis_proposals_kind", "kind BETWEEN 0 AND 4");
                table.CheckConstraint("CK_inbox_analysis_proposals_position", "position >= 0 AND position < 20");
                table.ForeignKey(
                    name: "FK_inbox_analysis_proposals_inbox_analyses_analysis_id",
                    column: x => x.analysis_id,
                    principalTable: "inbox_analyses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_inbox_analyses_inbox_item_id_user_id",
            table: "inbox_analyses",
            columns: new[] { "inbox_item_id", "user_id" });

        migrationBuilder.CreateIndex(
            name: "IX_inbox_analyses_inbox_item_id_version",
            table: "inbox_analyses",
            columns: new[] { "inbox_item_id", "version" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_inbox_analyses_user_id_inbox_item_id",
            table: "inbox_analyses",
            columns: new[] { "user_id", "inbox_item_id" });

        migrationBuilder.CreateIndex(
            name: "IX_inbox_analysis_proposals_analysis_id_position",
            table: "inbox_analysis_proposals",
            columns: new[] { "analysis_id", "position" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "inbox_analysis_proposals");

        migrationBuilder.DropTable(
            name: "inbox_analyses");

        migrationBuilder.DropUniqueConstraint(
            name: "AK_inbox_items_id_user_id",
            table: "inbox_items");
    }
}
