using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxTelegramFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reply_markup_json",
                table: "telegram_outbox_messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "confirmation_outcome",
                table: "inbox_automatic_applications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confirmed_targets",
                table: "inbox_automatic_applications",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTime>(
                name: "resolved_at",
                table: "inbox_automatic_applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "telegram_inbox_destinations",
                columns: table => new
                {
                    inbox_item_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    locale = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telegram_inbox_destinations", x => x.inbox_item_id);
                    table.ForeignKey(
                        name: "FK_telegram_inbox_destinations_inbox_items_inbox_item_id",
                        column: x => x.inbox_item_id,
                        principalTable: "inbox_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "telegram_inbox_destinations");

            migrationBuilder.DropColumn(
                name: "reply_markup_json",
                table: "telegram_outbox_messages");

            migrationBuilder.DropColumn(
                name: "confirmation_outcome",
                table: "inbox_automatic_applications");

            migrationBuilder.DropColumn(
                name: "confirmed_targets",
                table: "inbox_automatic_applications");

            migrationBuilder.DropColumn(
                name: "resolved_at",
                table: "inbox_automatic_applications");
        }
    }
}
