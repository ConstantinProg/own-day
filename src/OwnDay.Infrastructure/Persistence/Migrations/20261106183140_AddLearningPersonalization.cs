using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLearningPersonalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Teaching",
                table: "learning_lessons",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transfer",
                table: "learning_lessons",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "learning_preferences",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_preferences", x => new { x.UserId, x.Kind });
                    table.CheckConstraint("CK_learning_preference_kind", "\"Kind\" BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_learning_preference_owner", "\"UserId\" > 0");
                    table.CheckConstraint("CK_learning_preference_value", "length(trim(\"Value\")) BETWEEN 1 AND 200");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learning_preferences");

            migrationBuilder.DropColumn(
                name: "Teaching",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "Transfer",
                table: "learning_lessons");
        }
    }
}
