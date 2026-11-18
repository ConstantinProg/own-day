using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118180000_AddAssessmentReviews")]
public sealed class AddAssessmentReviews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ReviewHistory", "learning_evidence", type: "jsonb", nullable: true);
        migrationBuilder.AddColumn<bool>("ReassessmentCredit", "learning_evidence", type: "boolean", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_evidence WHERE "ReviewHistory" IS NOT NULL OR "ReassessmentCredit" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Retain assessment reviews; roll back to a compatible reader';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("ReviewHistory", "learning_evidence");
        migrationBuilder.DropColumn("ReassessmentCredit", "learning_evidence");
    }
}
