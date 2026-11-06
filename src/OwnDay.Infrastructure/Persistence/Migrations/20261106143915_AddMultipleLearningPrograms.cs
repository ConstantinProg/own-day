using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMultipleLearningPrograms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_learning_programs_UserId",
                table: "learning_programs");

            migrationBuilder.CreateTable(
                name: "learning_contexts",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_contexts", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_learning_contexts_learning_programs_ProgramId_UserId",
                        columns: x => new { x.ProgramId, x.UserId },
                        principalTable: "learning_programs",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_program_skill_links",
                columns: table => new
                {
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgressVersion = table.Column<int>(type: "integer", nullable: false),
                    TopicCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Trust = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_program_skill_links", x => new { x.ProgramId, x.ProgressVersion, x.TopicCode });
                    table.ForeignKey(
                        name: "FK_learning_program_skill_links_learning_programs_ProgramId_Us~",
                        columns: x => new { x.ProgramId, x.UserId },
                        principalTable: "learning_programs",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_program_skill_links_learning_routes_ProgramId_Prog~",
                        columns: x => new { x.ProgramId, x.ProgressVersion },
                        principalTable: "learning_routes",
                        principalColumns: new[] { "ProgramId", "Version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_learning_program_skill_links_learning_skills_SkillId_UserId",
                        columns: x => new { x.SkillId, x.UserId },
                        principalTable: "learning_skills",
                        principalColumns: new[] { "Id", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_programs_UserId",
                table: "learning_programs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_learning_contexts_ProgramId_UserId",
                table: "learning_contexts",
                columns: new[] { "ProgramId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_program_skill_links_ProgramId_UserId",
                table: "learning_program_skill_links",
                columns: new[] { "ProgramId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_program_skill_links_SkillId_UserId",
                table: "learning_program_skill_links",
                columns: new[] { "SkillId", "UserId" });

            // Before this migration each owner has at most one program; retain that selection.
            migrationBuilder.Sql("""
                INSERT INTO learning_contexts ("UserId", "ProgramId")
                SELECT "UserId", "Id" FROM learning_programs;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learning_contexts");

            migrationBuilder.DropTable(
                name: "learning_program_skill_links");

            migrationBuilder.DropIndex(
                name: "IX_learning_programs_UserId",
                table: "learning_programs");

            migrationBuilder.CreateIndex(
                name: "IX_learning_programs_UserId",
                table: "learning_programs",
                column: "UserId",
                unique: true);
        }
    }
}
