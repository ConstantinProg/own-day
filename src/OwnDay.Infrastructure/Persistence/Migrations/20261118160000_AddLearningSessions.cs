using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118160000_AddLearningSessions")]
public sealed class AddLearningSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("learning_sessions", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            UserId = table.Column<long>(type: "bigint", nullable: false),
            ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
            StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            EndReason = table.Column<int>(type: "integer", nullable: true),
            ActiveSeconds = table.Column<long>(type: "bigint", nullable: false),
            FirstSourceOrder = table.Column<long>(type: "bigint", nullable: false),
            Summary = table.Column<string>(type: "jsonb", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_learning_sessions", row => row.Id);
            table.ForeignKey("FK_learning_sessions_program", row => new { row.ProgramId, row.UserId },
                "learning_programs", principalColumns: ["Id", "UserId"], onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_learning_sessions_ProgramId_UserId", "learning_sessions", ["ProgramId", "UserId"]);
        migrationBuilder.CreateIndex("IX_learning_sessions_UserId_StartedAt_FirstSourceOrder", "learning_sessions",
            ["UserId", "StartedAt", "FirstSourceOrder"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_sessions) THEN
                    RAISE EXCEPTION 'Retain study session history; roll back to a compatible reader';
                END IF;
            END $$;
            """);
        migrationBuilder.DropTable("learning_sessions");
    }
}
