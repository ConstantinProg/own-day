using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261122091000_AddDiagnosticJournalAndExports")]
public sealed class AddDiagnosticJournalAndExports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("document_name", "telegram_outbox_messages", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTime>("document_expires_at", "telegram_outbox_messages", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<Guid>("diagnostic_correlation_id", "telegram_outbox_messages", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<long>("diagnostic_owner_id", "processed_telegram_updates", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<Guid>("diagnostic_correlation_id", "processed_telegram_updates", type: "uuid", nullable: true);
        migrationBuilder.CreateIndex("IX_processed_telegram_updates_diagnostic_owner",
            "processed_telegram_updates", ["diagnostic_owner_id", "received_at", "update_id"], filter: "diagnostic_owner_id IS NOT NULL");
        migrationBuilder.CreateTable("diagnostic_events", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OwnerId = table.Column<long>(type: "bigint", nullable: false),
            Generation = table.Column<Guid>(type: "uuid", nullable: false),
            OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            Category = table.Column<string>(type: "text", nullable: false),
            Code = table.Column<string>(type: "text", nullable: false),
            Severity = table.Column<string>(type: "text", nullable: false),
            CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
            RequestId = table.Column<Guid>(type: "uuid", nullable: true),
            AttemptNumber = table.Column<int>(type: "integer", nullable: true),
            EntityId = table.Column<Guid>(type: "uuid", nullable: true),
            SchemaVersion = table.Column<int>(type: "integer", nullable: false),
            Detail = table.Column<string>(type: "text", nullable: false),
            DetailGeneration = table.Column<Guid>(type: "uuid", nullable: false),
            Data = table.Column<string>(type: "jsonb", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_diagnostic_events", row => row.Id));
        migrationBuilder.CreateIndex("IX_diagnostic_events_OwnerId_OccurredAt_Id", "diagnostic_events", ["OwnerId", "OccurredAt", "Id"]);
        migrationBuilder.CreateIndex("IX_diagnostic_events_OwnerId_RequestId_OccurredAt_Id", "diagnostic_events", ["OwnerId", "RequestId", "OccurredAt", "Id"]);
        migrationBuilder.CreateIndex("IX_diagnostic_events_OwnerId_CorrelationId", "diagnostic_events", ["OwnerId", "CorrelationId"]);
        migrationBuilder.CreateIndex("IX_diagnostic_events_OccurredAt_Id", "diagnostic_events", ["OccurredAt", "Id"]);
        migrationBuilder.CreateTable("diagnostic_fences", columns: table => new
        {
            OwnerId = table.Column<long>(type: "bigint", nullable: false),
            Generation = table.Column<Guid>(type: "uuid", nullable: false),
            DetailGeneration = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_diagnostic_fences", row => row.OwnerId));
        migrationBuilder.CreateTable("diagnostic_export_jobs", columns: table => new
        {
            UpdateId = table.Column<int>(type: "integer", nullable: false),
            OwnerId = table.Column<long>(type: "bigint", nullable: false),
            Mode = table.Column<int>(type: "integer", nullable: false),
            ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            InRussian = table.Column<bool>(type: "boolean", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_diagnostic_export_jobs", row => row.UpdateId));
        migrationBuilder.CreateIndex("IX_diagnostic_export_jobs_OwnerId", "diagnostic_export_jobs", "OwnerId", unique: true);
        migrationBuilder.CreateIndex("IX_diagnostic_export_jobs_ReceivedAt_UpdateId", "diagnostic_export_jobs", ["ReceivedAt", "UpdateId"]);
        migrationBuilder.CreateTable("diagnostic_documents", columns: table => new
        {
            OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
            Contents = table.Column<byte[]>(type: "bytea", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_diagnostic_documents", row => row.OutboxId);
            table.ForeignKey("FK_diagnostic_documents_telegram_outbox_messages_OutboxId", row => row.OutboxId,
                "telegram_outbox_messages", "id", onDelete: ReferentialAction.Cascade);
        });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM telegram_outbox_messages WHERE document_name IS NOT NULL");
        migrationBuilder.DropTable("diagnostic_documents");
        migrationBuilder.DropTable("diagnostic_export_jobs");
        migrationBuilder.DropTable("diagnostic_fences");
        migrationBuilder.DropTable("diagnostic_events");
        migrationBuilder.DropColumn("document_name", "telegram_outbox_messages");
        migrationBuilder.DropColumn("document_expires_at", "telegram_outbox_messages");
        migrationBuilder.DropColumn("diagnostic_correlation_id", "telegram_outbox_messages");
        migrationBuilder.DropIndex("IX_processed_telegram_updates_diagnostic_owner", "processed_telegram_updates");
        migrationBuilder.DropColumn("diagnostic_correlation_id", "processed_telegram_updates");
        migrationBuilder.DropColumn("diagnostic_owner_id", "processed_telegram_updates");
    }
}
