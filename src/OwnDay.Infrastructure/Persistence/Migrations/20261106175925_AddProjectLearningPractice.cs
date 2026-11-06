using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectLearningPractice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ProjectActionId",
                table: "learning_lessons",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_actions_id_user_id",
                table: "actions",
                columns: new[] { "id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_lessons_ProjectActionId_UserId",
                table: "learning_lessons",
                columns: new[] { "ProjectActionId", "UserId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_learning_lessons_project_action",
                table: "learning_lessons",
                sql: "\"ProjectActionId\" IS NULL OR \"ProgramId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_learning_lessons_actions_ProjectActionId_UserId",
                table: "learning_lessons",
                columns: new[] { "ProjectActionId", "UserId" },
                principalTable: "actions",
                principalColumns: new[] { "id", "user_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_learning_lessons_actions_ProjectActionId_UserId",
                table: "learning_lessons");

            migrationBuilder.DropIndex(
                name: "IX_learning_lessons_ProjectActionId_UserId",
                table: "learning_lessons");

            migrationBuilder.DropCheckConstraint(
                name: "CK_learning_lessons_project_action",
                table: "learning_lessons");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_actions_id_user_id",
                table: "actions");

            migrationBuilder.DropColumn(
                name: "ProjectActionId",
                table: "learning_lessons");
        }
    }
}
