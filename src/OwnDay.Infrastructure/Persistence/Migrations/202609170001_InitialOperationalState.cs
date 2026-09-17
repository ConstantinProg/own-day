using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("202609170001_InitialOperationalState")]
public partial class InitialOperationalState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "processed_telegram_updates",
            columns: table => new
            {
                update_id = table.Column<long>(type: "bigint", nullable: false),
                received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_processed_telegram_updates", x => x.update_id);
            });

        migrationBuilder.CreateTable(
            name: "telegram_outbox_messages",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                chat_id = table.Column<long>(type: "bigint", nullable: false),
                text = table.Column<string>(type: "text", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                attempt_count = table.Column<int>(type: "integer", nullable: false),
                next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                last_error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_telegram_outbox_messages", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_telegram_outbox_messages_status_next_attempt_at",
            table: "telegram_outbox_messages",
            columns: new[] { "status", "next_attempt_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "processed_telegram_updates");
        migrationBuilder.DropTable(name: "telegram_outbox_messages");
    }
}
