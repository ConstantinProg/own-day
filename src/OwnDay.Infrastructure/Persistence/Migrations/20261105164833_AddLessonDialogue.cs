using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonDialogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_learning_lessons_UserId",
                table: "learning_lessons");

            migrationBuilder.AddColumn<string>(
                name: "ContextSummary",
                table: "learning_lessons",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "learning_lessons",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE learning_lessons
                SET "Status" = CASE WHEN "Answer" IS NULL THEN 0 ELSE 2 END,
                    "ContextSummary" = 'Topic: ' || "Topic" || E'\n' ||
                        CASE WHEN "Answer" IS NULL
                            THEN 'Expected action: Answer the current question.'
                            ELSE 'Lesson ended. Start another lesson.' END ||
                        E'\nCurrent question: ' || "Question";
                """);

            migrationBuilder.CreateTable(
                name: "learning_interactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Request = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Explanation = table.Column<string>(type: "character varying(2500)", maxLength: 2500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_interactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_learning_interactions_learning_lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "learning_lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_lessons_UserId",
                table: "learning_lessons",
                column: "UserId",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_learning_interactions_LessonId",
                table: "learning_interactions",
                column: "LessonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learning_interactions");

            migrationBuilder.DropIndex(
                name: "IX_learning_lessons_UserId",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "ContextSummary",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "learning_lessons");

            migrationBuilder.CreateIndex(
                name: "IX_learning_lessons_UserId",
                table: "learning_lessons",
                column: "UserId",
                unique: true,
                filter: "\"Answer\" IS NULL");
        }
    }
}
