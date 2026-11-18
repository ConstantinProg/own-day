using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118130000_AddLearningGoalCriterion")]
public sealed class AddLearningGoalCriterion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("GoalCriterion", "learning_programs",
            type: "character varying(500)", maxLength: 500, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_programs WHERE "GoalCriterion" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Retain learning goal criteria; roll back to a compatible reader';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("GoalCriterion", "learning_programs");
    }
}
