using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118140000_AddSubjectDiagnostic")]
public sealed class AddSubjectDiagnostic : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("SubjectDiagnostic", "learning_programs", type: "jsonb", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_programs WHERE "SubjectDiagnostic" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Retain diagnostic observations; roll back to a compatible reader';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("SubjectDiagnostic", "learning_programs");
    }
}
