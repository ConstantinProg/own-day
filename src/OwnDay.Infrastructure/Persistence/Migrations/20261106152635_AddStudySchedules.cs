using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStudySchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "study_invitation_id",
                table: "telegram_outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "learning_study_schedules",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EveryDays = table.Column<int>(type: "integer", nullable: false),
                    Windows = table.Column<string>(type: "jsonb", nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    NextAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastInvitedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: true),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_study_schedules", x => x.UserId);
                    table.CheckConstraint("CK_learning_study_frequency", "\"EveryDays\" BETWEEN 1 AND 30");
                    table.CheckConstraint("CK_learning_study_invitation", "(\"InvitationId\" IS NULL AND \"ProgramId\" IS NULL AND \"SkillId\" IS NULL) OR (\"InvitationId\" IS NOT NULL AND ((\"ProgramId\" IS NOT NULL AND \"SkillId\" IS NULL) OR (\"ProgramId\" IS NULL AND \"SkillId\" IS NOT NULL)))");
                    table.CheckConstraint("CK_learning_study_owner", "\"UserId\" > 0");
                    table.ForeignKey(
                        name: "FK_learning_study_schedules_learning_programs_ProgramId_UserId",
                        columns: x => new { x.ProgramId, x.UserId },
                        principalTable: "learning_programs",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_study_schedules_learning_skills_SkillId_UserId",
                        columns: x => new { x.SkillId, x.UserId },
                        principalTable: "learning_skills",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_study_schedules_Paused_NextAt_UserId",
                table: "learning_study_schedules",
                columns: new[] { "Paused", "NextAt", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_study_schedules_ProgramId_UserId",
                table: "learning_study_schedules",
                columns: new[] { "ProgramId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_study_schedules_SkillId_UserId",
                table: "learning_study_schedules",
                columns: new[] { "SkillId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A downgraded host cannot validate invitation IDs. Retain history but retire pending invitations.
            migrationBuilder.Sql("""
                UPDATE telegram_outbox_messages SET status = 2, last_error = 'StudyScheduleRemoved'
                WHERE study_invitation_id IS NOT NULL AND status = 0
                """);

            migrationBuilder.DropTable(
                name: "learning_study_schedules");

            migrationBuilder.DropColumn(
                name: "study_invitation_id",
                table: "telegram_outbox_messages");
        }
    }
}
