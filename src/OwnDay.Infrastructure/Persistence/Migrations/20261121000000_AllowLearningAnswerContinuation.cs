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
        migrationBuilder.Sql($"""
            LOCK TABLE {Table} IN ACCESS EXCLUSIVE MODE;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM {Table} WHERE ordinal = 2) THEN
                    RAISE EXCEPTION 'Cannot downgrade the Learning schema while third provider steps are stored. Keep the additive schema when rolling back the application.';
                END IF;
            END;
            $$;
            """);
        migrationBuilder.DropCheckConstraint(Constraint, Table);
        migrationBuilder.AddCheckConstraint(Constraint, Table, "ordinal BETWEEN 0 AND 1 AND version > 0");
    }
}
