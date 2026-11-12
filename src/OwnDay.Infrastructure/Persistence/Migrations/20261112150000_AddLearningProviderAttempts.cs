using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261112150000_AddLearningProviderAttempts")]
public sealed class AddLearningProviderAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddCheckConstraint("CK_learning_provider_steps_identity", "learning_provider_steps", "ordinal BETWEEN 0 AND 1 AND version > 0");
        migrationBuilder.AddCheckConstraint("CK_learning_provider_steps_result", "learning_provider_steps", "result IS NULL OR failure IS NULL");
        migrationBuilder.CreateIndex("IX_learning_requests_active_head", "learning_requests", new[] { "received_at", "user_id", "sequence" }, filter: "status IN (1, 2)");
        migrationBuilder.CreateSequence<long>("telegram_outbox_delivery_order_seq");
        migrationBuilder.AddColumn<long>("delivery_order", "telegram_outbox_messages", type: "bigint", nullable: true);
        migrationBuilder.Sql("""
            WITH ordered AS (SELECT id, row_number() OVER (ORDER BY created_at, id) AS position FROM telegram_outbox_messages)
            UPDATE telegram_outbox_messages message SET delivery_order = ordered.position FROM ordered WHERE message.id = ordered.id;
            SELECT setval('telegram_outbox_delivery_order_seq', COALESCE((SELECT max(delivery_order) + 1 FROM telegram_outbox_messages), 1), false);
            ALTER TABLE telegram_outbox_messages ALTER COLUMN delivery_order SET NOT NULL,
                ALTER COLUMN delivery_order SET DEFAULT nextval('telegram_outbox_delivery_order_seq');
            """);
        migrationBuilder.CreateIndex("IX_telegram_outbox_messages_delivery_order", "telegram_outbox_messages", "delivery_order", unique: true);
        migrationBuilder.CreateTable("learning_provider_attempts", columns: table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            request_id = table.Column<Guid>(type: "uuid", nullable: false),
            ordinal = table.Column<int>(type: "integer", nullable: false),
            number = table.Column<int>(type: "integer", nullable: false),
            lease_token = table.Column<Guid>(type: "uuid", nullable: false),
            started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_learning_provider_attempts", row => row.id);
            table.CheckConstraint("CK_learning_provider_attempts_state",
                "number BETWEEN 1 AND 3 AND ((finished_at IS NULL AND outcome IS NULL) OR (finished_at IS NOT NULL AND outcome IS NOT NULL))");
            table.ForeignKey("FK_learning_provider_attempts_learning_provider_steps_request_id_ordinal", row => new { row.request_id, row.ordinal },
                "learning_provider_steps", new[] { "request_id", "ordinal" }, onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_learning_provider_attempts_request_id_ordinal_number", "learning_provider_attempts",
            new[] { "request_id", "ordinal", "number" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("learning_provider_attempts");
        migrationBuilder.DropCheckConstraint("CK_learning_provider_steps_identity", "learning_provider_steps");
        migrationBuilder.DropCheckConstraint("CK_learning_provider_steps_result", "learning_provider_steps");
        migrationBuilder.DropIndex("IX_learning_requests_active_head", "learning_requests");
        migrationBuilder.DropIndex("IX_telegram_outbox_messages_delivery_order", "telegram_outbox_messages");
        migrationBuilder.DropColumn("delivery_order", "telegram_outbox_messages");
        migrationBuilder.DropSequence("telegram_outbox_delivery_order_seq");
    }
}
