using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261120110000_AddAdaptiveChoiceExercises")]
public sealed class AddAdaptiveChoiceExercises : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Choice", "learning_lessons", type: "jsonb", nullable: true);
        migrationBuilder.AddColumn<string>("ChoiceEvidence", "learning_evidence", type: "jsonb", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ChoiceEvidence", "learning_evidence");
        migrationBuilder.DropColumn("Choice", "learning_lessons");
    }
}
