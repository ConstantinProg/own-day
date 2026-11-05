using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLessonContextSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextSummary",
                table: "learning_lessons");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContextSummary",
                table: "learning_lessons",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE learning_lessons
                SET "ContextSummary" = 'Topic: ' || "Topic" || E'\n' ||
                    CASE "Status"
                        WHEN 0 THEN 'Expected action: Answer the current question.'
                        WHEN 1 THEN 'Expected action: Resume the lesson before answering.'
                        ELSE 'Lesson ended. Start another lesson.' END ||
                    E'\nCurrent question: ' || "Question";
                """);
        }
    }
}
