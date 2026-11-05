using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLearnerKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SkillId",
                table: "learning_lessons",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_learning_lessons_Id_UserId",
                table: "learning_lessons",
                columns: new[] { "Id", "UserId" });

            migrationBuilder.CreateTable(
                name: "learning_skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_skills", x => x.Id);
                    table.UniqueConstraint("AK_learning_skills_Id_UserId", x => new { x.Id, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "learning_evidence",
                columns: table => new
                {
                    LessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Answer = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: false),
                    AssessmentFeedback = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: false),
                    AssessedCorrect = table.Column<bool>(type: "boolean", nullable: false),
                    AssessmentSource = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_evidence", x => x.LessonId);
                    table.ForeignKey(
                        name: "FK_learning_evidence_learning_lessons_LessonId_UserId",
                        columns: x => new { x.LessonId, x.UserId },
                        principalTable: "learning_lessons",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_evidence_learning_skills_SkillId_UserId",
                        columns: x => new { x.SkillId, x.UserId },
                        principalTable: "learning_skills",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_lessons_SkillId_UserId",
                table: "learning_lessons",
                columns: new[] { "SkillId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_evidence_LessonId_UserId",
                table: "learning_evidence",
                columns: new[] { "LessonId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_evidence_SkillId_UserId",
                table: "learning_evidence",
                columns: new[] { "SkillId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_skills_UserId",
                table: "learning_skills",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_learning_lessons_learning_skills_SkillId_UserId",
                table: "learning_lessons",
                columns: new[] { "SkillId", "UserId" },
                principalTable: "learning_skills",
                principalColumns: new[] { "Id", "UserId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_learning_lessons_learning_skills_SkillId_UserId",
                table: "learning_lessons");

            migrationBuilder.DropTable(
                name: "learning_evidence");

            migrationBuilder.DropTable(
                name: "learning_skills");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_learning_lessons_Id_UserId",
                table: "learning_lessons");

            migrationBuilder.DropIndex(
                name: "IX_learning_lessons_SkillId_UserId",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "SkillId",
                table: "learning_lessons");
        }
    }
}
