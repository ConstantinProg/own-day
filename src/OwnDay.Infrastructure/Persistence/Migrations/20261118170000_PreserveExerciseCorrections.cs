using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118170000_PreserveExerciseCorrections")]
public sealed class PreserveExerciseCorrections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_lessons WHERE "RubricEvaluation" ? 'corrections')
                    OR EXISTS (SELECT 1 FROM learning_evidence WHERE "RubricEvidence"->'evaluation' ? 'corrections')
                    OR EXISTS (SELECT 1 FROM learning_interactions WHERE "Kind" = 2) THEN
                    RAISE EXCEPTION 'Retain correction and solution history; roll back to a compatible reader';
                END IF;
            END $$;
            """);
}
