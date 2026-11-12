using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261112200000_AddDialogueContexts")]
public sealed class AddDialogueContexts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("dialogue_outcome", "learning_requests", type: "jsonb", nullable: true);
        migrationBuilder.CreateIndex("IX_learning_requests_dialogue_owner", "learning_requests", "user_id",
            filter: "payload_kind = 'learning_text' OR (payload_kind = 'learning_command' AND payload_version = 3)");
        migrationBuilder.CreateTable("dialogue_contexts", columns: table => new
        {
            user_id = table.Column<long>(type: "bigint", nullable: false),
            revision = table.Column<long>(type: "bigint", nullable: false),
            schema_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
            phase = table.Column<int>(type: "integer", nullable: false),
            expected_response = table.Column<int>(type: "integer", nullable: false),
            prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
            target_lesson_id = table.Column<Guid>(type: "uuid", nullable: true),
            question_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
            prompt_opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            interrupted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            interrupted_source_order = table.Column<long>(type: "bigint", nullable: false),
            response_after_source_order = table.Column<long>(type: "bigint", nullable: false),
            deferred_goal_request_id = table.Column<Guid>(type: "uuid", nullable: true),
            pending_clarification = table.Column<string>(type: "jsonb", nullable: true),
            pending_confirmation = table.Column<string>(type: "jsonb", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_dialogue_contexts", row => row.user_id);
            table.CheckConstraint("CK_dialogue_contexts_identity", "user_id > 0 AND revision > 0 AND schema_version > 0");
        });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN IF EXISTS (SELECT 1 FROM dialogue_contexts) OR EXISTS
                (SELECT 1 FROM learning_requests WHERE dialogue_outcome IS NOT NULL OR (status IN (1, 2) AND
                    (payload_kind = 'learning_text' OR (payload_kind = 'learning_command' AND payload_version = 3)))) THEN
                RAISE EXCEPTION 'Retain dialogue and pending states; roll back code only to a compatible reader';
            END IF; END $$;
            """);
        migrationBuilder.DropTable("dialogue_contexts");
        migrationBuilder.DropIndex("IX_learning_requests_dialogue_owner", "learning_requests");
        migrationBuilder.DropColumn("dialogue_outcome", "learning_requests");
    }
}
