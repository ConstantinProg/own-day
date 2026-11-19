using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118200000_ScopeLearningPreferences")]
public sealed class ScopeLearningPreferences : Migration
{
    private const string Table = "learning_preferences";
    private const string Key = "PK_learning_preferences";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Scope", Table, type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<long>("ResetThrough", Table, type: "bigint", nullable: true);
        migrationBuilder.DropPrimaryKey(Key, Table);
        migrationBuilder.AddPrimaryKey(Key, Table, ["UserId", "Kind", "Scope"]);
        migrationBuilder.AddColumn<string>("ExplanationPreferences", "learning_interactions", type: "jsonb", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_preferences WHERE "Scope" <> '' OR "ResetThrough" IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM learning_interactions WHERE "ExplanationPreferences" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Retain scoped learning preferences; roll back to a compatible reader';
                END IF;
            END $$;
            """);
        migrationBuilder.DropPrimaryKey(Key, Table);
        migrationBuilder.DropColumn("Scope", Table);
        migrationBuilder.DropColumn("ResetThrough", Table);
        migrationBuilder.DropColumn("ExplanationPreferences", "learning_interactions");
        migrationBuilder.AddPrimaryKey(Key, Table, ["UserId", "Kind"]);
    }
}
