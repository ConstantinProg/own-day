using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLearningRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "learning_request_id",
                table: "telegram_outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reply_ordinal",
                table: "telegram_outbox_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "learning_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    source_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_update_id = table.Column<int>(type: "integer", nullable: false),
                    source_message_id = table.Column<int>(type: "integer", nullable: true),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    payload_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    payload_version = table.Column<int>(type: "integer", nullable: false),
                    normalized_input = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_requests", x => x.id);
                    table.CheckConstraint("CK_learning_requests_completion", "status = 0 AND completed_at >= received_at AND outcome = 'reply_committed'");
                    table.CheckConstraint("CK_learning_requests_identity", "user_id > 0 AND sequence > 0 AND payload_version > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_telegram_outbox_messages_learning_request_id_reply_ordinal",
                table: "telegram_outbox_messages",
                columns: new[] { "learning_request_id", "reply_ordinal" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_telegram_outbox_messages_learning_reply",
                table: "telegram_outbox_messages",
                sql: "(learning_request_id IS NULL AND reply_ordinal IS NULL) OR (learning_request_id IS NOT NULL AND reply_ordinal IS NOT NULL AND reply_ordinal >= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_learning_requests_source_key",
                table: "learning_requests",
                column: "source_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_requests_user_id_sequence",
                table: "learning_requests",
                columns: new[] { "user_id", "sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_telegram_outbox_messages_learning_requests_learning_request~",
                table: "telegram_outbox_messages",
                column: "learning_request_id",
                principalTable: "learning_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_telegram_outbox_messages_learning_requests_learning_request~",
                table: "telegram_outbox_messages");

            migrationBuilder.DropTable(
                name: "learning_requests");

            migrationBuilder.DropIndex(
                name: "IX_telegram_outbox_messages_learning_request_id_reply_ordinal",
                table: "telegram_outbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_telegram_outbox_messages_learning_reply",
                table: "telegram_outbox_messages");

            migrationBuilder.DropColumn(
                name: "learning_request_id",
                table: "telegram_outbox_messages");

            migrationBuilder.DropColumn(
                name: "reply_ordinal",
                table: "telegram_outbox_messages");
        }
    }
}
