using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLearningPrograms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProgramId",
                table: "learning_lessons",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RouteVersion",
                table: "learning_lessons",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TopicCode",
                table: "learning_lessons",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "learning_programs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Experience = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Discipline = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    DiagnosticQuestion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DiagnosticAnswer = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: true),
                    DiagnosticPending = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_programs", x => x.Id);
                    table.UniqueConstraint("AK_learning_programs_Id_UserId", x => new { x.Id, x.UserId });
                    table.ForeignKey(
                        name: "FK_learning_programs_projects_ProjectId_UserId",
                        columns: x => new { x.ProjectId, x.UserId },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Topics = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_routes", x => x.Id);
                    table.UniqueConstraint("AK_learning_routes_ProgramId_Version", x => new { x.ProgramId, x.Version });
                    table.ForeignKey(
                        name: "FK_learning_routes_learning_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "learning_programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_lessons_ProgramId_RouteVersion",
                table: "learning_lessons",
                columns: new[] { "ProgramId", "RouteVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_lessons_ProgramId_UserId",
                table: "learning_lessons",
                columns: new[] { "ProgramId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_programs_ProjectId_UserId",
                table: "learning_programs",
                columns: new[] { "ProjectId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_programs_UserId",
                table: "learning_programs",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_learning_lessons_learning_programs_ProgramId_UserId",
                table: "learning_lessons",
                columns: new[] { "ProgramId", "UserId" },
                principalTable: "learning_programs",
                principalColumns: new[] { "Id", "UserId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_learning_lessons_learning_routes_ProgramId_RouteVersion",
                table: "learning_lessons",
                columns: new[] { "ProgramId", "RouteVersion" },
                principalTable: "learning_routes",
                principalColumns: new[] { "ProgramId", "Version" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_learning_lessons_learning_programs_ProgramId_UserId",
                table: "learning_lessons");

            migrationBuilder.DropForeignKey(
                name: "FK_learning_lessons_learning_routes_ProgramId_RouteVersion",
                table: "learning_lessons");

            migrationBuilder.DropTable(
                name: "learning_routes");

            migrationBuilder.DropTable(
                name: "learning_programs");

            migrationBuilder.DropIndex(
                name: "IX_learning_lessons_ProgramId_RouteVersion",
                table: "learning_lessons");

            migrationBuilder.DropIndex(
                name: "IX_learning_lessons_ProgramId_UserId",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "ProgramId",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "RouteVersion",
                table: "learning_lessons");

            migrationBuilder.DropColumn(
                name: "TopicCode",
                table: "learning_lessons");
        }
    }
}
