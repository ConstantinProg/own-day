using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261114090000_AddEnglishDiagnostic")]
public sealed class AddEnglishDiagnostic : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("EnglishDiagnostic", "learning_programs", type: "jsonb", nullable: true);
        migrationBuilder.AddColumn<string>("diagnostic_target", "dialogue_contexts", type: "jsonb", nullable: true);
        foreach (var table in new[] { "learning_lessons", "learning_evidence" })
        {
            // Legacy independence cannot be reconstructed from a missing history.
            migrationBuilder.AddColumn<int>("Assistance", table, type: "integer", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>("Content", table, type: "jsonb", nullable: true);
        }
        migrationBuilder.AlterColumn<int>("schema_version", "dialogue_contexts", type: "integer", nullable: false,
            defaultValue: 3, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 2);
        migrationBuilder.Sql("UPDATE dialogue_contexts SET schema_version = 3 WHERE schema_version = 2");
        // The old streak did not distinguish assisted/unknown answers. Preserve
        // evidence and due dates, but do not carry it into independent credit.
        migrationBuilder.Sql("UPDATE learning_reviews SET \"SuccessfulReviews\" = 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN IF
                EXISTS (SELECT 1 FROM learning_programs WHERE "EnglishDiagnostic" IS NOT NULL) OR
                EXISTS (SELECT 1 FROM dialogue_contexts WHERE diagnostic_target IS NOT NULL) OR
                EXISTS (SELECT 1 FROM learning_lessons WHERE "Content" IS NOT NULL OR "Assistance" <> 0) OR
                EXISTS (SELECT 1 FROM learning_evidence WHERE "Content" IS NOT NULL OR "Assistance" <> 0)
            THEN RAISE EXCEPTION 'Retain diagnostic results and assistance; roll back to a compatible reader';
            END IF; END $$;
            UPDATE dialogue_contexts SET schema_version = 2 WHERE schema_version = 3;
            """);
        migrationBuilder.AlterColumn<int>("schema_version", "dialogue_contexts", type: "integer", nullable: false,
            defaultValue: 2, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 3);
        migrationBuilder.DropColumn("EnglishDiagnostic", "learning_programs");
        migrationBuilder.DropColumn("diagnostic_target", "dialogue_contexts");
        foreach (var table in new[] { "learning_lessons", "learning_evidence" })
        {
            migrationBuilder.DropColumn("Assistance", table);
            migrationBuilder.DropColumn("Content", table);
        }
    }
}
