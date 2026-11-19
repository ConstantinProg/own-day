using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118220000_TrackExplanationProvenance")]
public sealed class TrackExplanationProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("Provenance", "learning_interactions", type: "jsonb", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_interactions WHERE "Provenance" IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM learning_lessons WHERE COALESCE("Provenance" ->> 'Status', "Provenance" ->> 'status') IN ('4', '5')) THEN
                    RAISE EXCEPTION 'Retain learning source provenance; roll back to a compatible reader';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("Provenance", "learning_interactions");
    }
}
