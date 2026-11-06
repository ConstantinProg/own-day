using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpacedReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReview",
                table: "learning_lessons",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsReview",
                table: "learning_evidence",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "learning_reviews",
                columns: table => new
                {
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuccessfulReviews = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_reviews", x => x.SkillId);
                    table.ForeignKey(
                        name: "FK_learning_reviews_learning_skills_SkillId_UserId",
                        columns: x => new { x.SkillId, x.UserId },
                        principalTable: "learning_skills",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_reviews_SkillId_UserId",
                table: "learning_reviews",
                columns: new[] { "SkillId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_reviews_UserId_DueAt_SkillId",
                table: "learning_reviews",
                columns: new[] { "UserId", "DueAt", "SkillId" });

            // Historical answers establish a first deadline, never invented delayed-review success.
            migrationBuilder.Sql("""
                INSERT INTO learning_reviews ("SkillId", "UserId", "DueAt", "LastObservedAt", "LastLessonId", "SuccessfulReviews")
                SELECT "SkillId", "UserId",
                    "ObservedAt" + CASE WHEN "AssessedCorrect" THEN INTERVAL '24 hours' ELSE INTERVAL '1 hour' END,
                    "ObservedAt", "LessonId", 0
                FROM (
                    SELECT DISTINCT ON ("SkillId") "SkillId", "UserId", "ObservedAt", "LessonId", "AssessedCorrect"
                    FROM learning_evidence
                    ORDER BY "SkillId", "ObservedAt" DESC, "LessonId" DESC
                ) latest
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learning_reviews");

            migrationBuilder.DropColumn(
                name: "IsReview",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "IsReview",
                table: "learning_evidence");
        }
    }
}
