using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261121000000_AllowLearningAnswerContinuation")]
public sealed class AllowLearningAnswerContinuation : Migration
{
    private const string Table = "learning_provider_steps";
    private const string Constraint = "CK_learning_provider_steps_identity";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(Constraint, Table);
        migrationBuilder.AddCheckConstraint(Constraint, Table,
            "(ordinal BETWEEN 0 AND 1 OR (ordinal = 2 AND kind = 'generate_followup')) AND version > 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(Constraint, Table);
        migrationBuilder.AddCheckConstraint(Constraint, Table, "ordinal BETWEEN 0 AND 1 AND version > 0");
    }
}
