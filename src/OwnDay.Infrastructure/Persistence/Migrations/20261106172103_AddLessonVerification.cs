using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiIsCorrect",
                table: "learning_lessons",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssessmentStatus",
                table: "learning_lessons",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "DeterministicIsCorrect",
                table: "learning_lessons",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedAnswer",
                table: "learning_lessons",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "learning_lessons",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AiAssessedCorrect",
                table: "learning_evidence",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssessmentStatus",
                table: "learning_evidence",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "DeterministicIsCorrect",
                table: "learning_evidence",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE learning_lessons SET "AiIsCorrect" = "IsCorrect" WHERE "Status" = 2;
                UPDATE learning_evidence SET "AiAssessedCorrect" = "AssessedCorrect";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Losing conflict metadata would turn uncertain evidence into a definitive negative.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM learning_evidence WHERE "AssessmentStatus" = 2)
                       OR EXISTS (SELECT 1 FROM learning_lessons WHERE "AssessmentStatus" = 2) THEN
                        RAISE EXCEPTION 'Cannot remove verification metadata while conflicting assessments exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "AiIsCorrect",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "AssessmentStatus",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "DeterministicIsCorrect",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "ExpectedAnswer",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "AiAssessedCorrect",
                table: "learning_evidence");

            migrationBuilder.DropColumn(
                name: "AssessmentStatus",
                table: "learning_evidence");

            migrationBuilder.DropColumn(
                name: "DeterministicIsCorrect",
                table: "learning_evidence");
        }
    }
}
