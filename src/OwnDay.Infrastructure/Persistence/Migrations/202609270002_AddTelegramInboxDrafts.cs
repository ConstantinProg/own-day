using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("202609270002_AddTelegramInboxDrafts")]
public partial class AddTelegramInboxDrafts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE telegram_inbox_drafts (
                user_id bigint CONSTRAINT "PK_telegram_inbox_drafts" PRIMARY KEY,
                inbox_item_id bigint NOT NULL,
                step integer NOT NULL,
                target_kind integer NULL,
                text text NULL,
                source text NULL,
                project_id bigint NULL,
                version bigint NOT NULL,
                expires_at timestamp with time zone NOT NULL,
                CONSTRAINT "CK_telegram_inbox_drafts_user" CHECK (user_id > 0),
                CONSTRAINT "CK_telegram_inbox_drafts_step" CHECK (step BETWEEN 0 AND 4),
                CONSTRAINT "CK_telegram_inbox_drafts_target" CHECK (target_kind IS NULL OR target_kind BETWEEN 0 AND 4),
                CONSTRAINT "CK_telegram_inbox_drafts_version" CHECK (version >= 0)
            );
            CREATE INDEX "IX_telegram_inbox_drafts_expires_at" ON telegram_inbox_drafts (expires_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE telegram_inbox_drafts;");
    }
}
