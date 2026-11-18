using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118150000_AddExerciseRubrics")]
public sealed class AddExerciseRubrics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("RubricEvaluation", "learning_lessons", type: "jsonb", nullable: true);
        migrationBuilder.AddColumn<string>("RubricEvidence", "learning_evidence", type: "jsonb", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_lessons WHERE "RubricEvaluation" IS NOT NULL OR "Content"->'StructuredRubric' IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM learning_evidence WHERE "RubricEvidence" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Retain published and applied rubrics; roll back to a compatible reader';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("RubricEvaluation", "learning_lessons");
        migrationBuilder.DropColumn("RubricEvidence", "learning_evidence");
    }
}
