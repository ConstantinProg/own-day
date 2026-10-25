using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxAutomaticProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbox_automatic_applications",
                columns: table => new
                {
                    analysis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inbox_item_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    evaluated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    route = table.Column<int>(type: "integer", nullable: false),
                    decision = table.Column<string>(type: "jsonb", nullable: false),
                    proposals = table.Column<string>(type: "jsonb", nullable: false),
                    targets = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_automatic_applications", x => x.analysis_id);
                    table.ForeignKey(
                        name: "FK_inbox_automatic_applications_inbox_analyses_analysis_id",
                        column: x => x.analysis_id,
                        principalTable: "inbox_analyses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inbox_automatic_applications_inbox_items_inbox_item_id_user~",
                        columns: x => new { x.inbox_item_id, x.user_id },
                        principalTable: "inbox_items",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inbox_targets",
                columns: table => new
                {
                    kind = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    inbox_item_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_targets", x => new { x.inbox_item_id, x.kind, x.target_id });
                    table.CheckConstraint("CK_inbox_targets_identity", "kind BETWEEN 0 AND 4 AND target_id > 0");
                    table.ForeignKey(
                        name: "FK_inbox_targets_inbox_items_inbox_item_id",
                        column: x => x.inbox_item_id,
                        principalTable: "inbox_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_automatic_applications_inbox_item_id_user_id",
                table: "inbox_automatic_applications",
                columns: new[] { "inbox_item_id", "user_id" });

            // Preserve all previously processed manual entries in the new collection.
            migrationBuilder.Sql("""
                INSERT INTO inbox_targets (inbox_item_id, kind, target_id)
                SELECT id, target_kind, target_id FROM inbox_items WHERE status = 1
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_automatic_applications");

            migrationBuilder.DropTable(
                name: "inbox_targets");
        }
    }
}
