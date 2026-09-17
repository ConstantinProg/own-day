using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("202609170002_AddOutboxCleanupIndex")]
public partial class AddOutboxCleanupIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_telegram_outbox_messages_status_sent_at",
            table: "telegram_outbox_messages",
            columns: new[] { "status", "sent_at" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_telegram_outbox_messages_status_sent_at",
            table: "telegram_outbox_messages");
    }
}
