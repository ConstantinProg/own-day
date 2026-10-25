using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddInboxObservability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "inbox_operation_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                InboxItemId = table.Column<long>(type: "bigint", nullable: false),
                Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: true),
                TargetId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox_operation_events", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "openai_request_attempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AnalysisId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                ReservedTokens = table.Column<long>(type: "bigint", nullable: false),
                ChargedTokens = table.Column<long>(type: "bigint", nullable: false),
                InputTokens = table.Column<int>(type: "integer", nullable: true),
                CachedInputTokens = table.Column<int>(type: "integer", nullable: true),
                OutputTokens = table.Column<int>(type: "integer", nullable: true),
                InputUsdPerMillion = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: true),
                CachedInputUsdPerMillion = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: true),
                OutputUsdPerMillion = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: true),
                EstimatedUsd = table.Column<decimal>(type: "numeric(20,12)", precision: 20, scale: 12, nullable: true),
                ProviderRequestId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_openai_request_attempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_openai_request_attempts_inbox_analyses_AnalysisId",
                    column: x => x.AnalysisId,
                    principalTable: "inbox_analyses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_inbox_operation_events_Kind_TargetId",
            table: "inbox_operation_events",
            columns: new[] { "Kind", "TargetId" },
            unique: true,
            filter: "\"Code\" = 'created'");

        migrationBuilder.CreateIndex(
            name: "IX_inbox_operation_events_UserId_At",
            table: "inbox_operation_events",
            columns: new[] { "UserId", "At" });

        migrationBuilder.CreateIndex(
            name: "IX_inbox_operation_events_UserId_Code_InboxItemId",
            table: "inbox_operation_events",
            columns: new[] { "UserId", "Code", "InboxItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_openai_request_attempts_AnalysisId_StartedAt",
            table: "openai_request_attempts",
            columns: new[] { "AnalysisId", "StartedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_openai_request_attempts_UserId_StartedAt",
            table: "openai_request_attempts",
            columns: new[] { "UserId", "StartedAt" });
        // Recover creation facts from existing current targets and immutable application mappings,
        // including entities already removed by Undo. Never invent refused Undo or provider attempts.
        migrationBuilder.Sql("""
            INSERT INTO inbox_operation_events ("Id", "UserId", "InboxItemId", "Code", "At")
            VALUES (gen_random_uuid(), 0, 0, 'observability_started', clock_timestamp());

            WITH historical AS (
                SELECT a.user_id, a.inbox_item_id, a.evaluated_at AS at,
                    (CASE t->>'kind' WHEN 'Action' THEN 0 WHEN 'Project' THEN 1 WHEN 'SomedayMaybe' THEN 2
                        WHEN 'Reference' THEN 3 WHEN 'WaitingFor' THEN 4 END) AS kind,
                    (t->>'targetId')::bigint AS target_id
                FROM inbox_automatic_applications a CROSS JOIN LATERAL jsonb_array_elements(a.targets) t
                UNION ALL
                SELECT a.user_id, a.inbox_item_id, a.resolved_at,
                    (CASE t->>'kind' WHEN 'Action' THEN 0 WHEN 'Project' THEN 1 WHEN 'SomedayMaybe' THEN 2
                        WHEN 'Reference' THEN 3 WHEN 'WaitingFor' THEN 4 END), (t->>'targetId')::bigint
                FROM inbox_automatic_applications a CROSS JOIN LATERAL jsonb_array_elements(a.confirmed_targets) t
                WHERE a.confirmation_outcome = 0
                UNION ALL
                SELECT i.user_id, i.id, i.processed_at, t.kind, t.target_id
                FROM inbox_items i JOIN inbox_targets t ON t.inbox_item_id = i.id WHERE i.status = 1
            )
            INSERT INTO inbox_operation_events ("Id", "UserId", "InboxItemId", "Code", "At", "Kind", "TargetId")
            SELECT gen_random_uuid(), user_id, inbox_item_id, 'created', at, kind, target_id
            FROM (SELECT DISTINCT ON (kind, target_id) * FROM historical
                WHERE kind IS NOT NULL AND at IS NOT NULL ORDER BY kind, target_id, at) h;

            INSERT INTO inbox_operation_events ("Id", "UserId", "InboxItemId", "Code", "At")
            SELECT gen_random_uuid(), i.user_id, i.id, 'manual', i.processed_at
            FROM inbox_items i WHERE i.status = 1 AND NOT EXISTS (
                SELECT 1 FROM inbox_automatic_applications a WHERE a.inbox_item_id = i.id AND a.undone_at IS NULL AND
                    (a.route = 0 OR a.confirmation_outcome = 0));
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "inbox_operation_events");

        migrationBuilder.DropTable(
            name: "openai_request_attempts");
    }
}
