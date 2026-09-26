using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("202609260001_RenameTasksToActions")]
public partial class RenameTasksToActions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameTable(name: "tasks", newName: "actions");
        migrationBuilder.RenameIndex(
            name: "IX_tasks_user_id_status_created_at_id",
            table: "actions",
            newName: "IX_actions_user_id_status_created_at_id");
        migrationBuilder.Sql("ALTER TABLE actions RENAME CONSTRAINT \"PK_tasks\" TO \"PK_actions\"");
        migrationBuilder.Sql("ALTER TABLE actions RENAME CONSTRAINT \"CK_tasks_status_completed_at\" TO \"CK_actions_status_completed_at\"");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE actions RENAME CONSTRAINT \"CK_actions_status_completed_at\" TO \"CK_tasks_status_completed_at\"");
        migrationBuilder.Sql("ALTER TABLE actions RENAME CONSTRAINT \"PK_actions\" TO \"PK_tasks\"");
        migrationBuilder.RenameIndex(
            name: "IX_actions_user_id_status_created_at_id",
            table: "actions",
            newName: "IX_tasks_user_id_status_created_at_id");
        migrationBuilder.RenameTable(name: "actions", newName: "tasks");
    }
}
