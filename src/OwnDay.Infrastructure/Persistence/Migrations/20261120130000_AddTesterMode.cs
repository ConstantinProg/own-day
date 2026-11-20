using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261120130000_AddTesterMode")]
public sealed class AddTesterMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable("tester_mode_users", columns: table => new
        {
            user_id = table.Column<long>(type: "bigint", nullable: false),
            authorized = table.Column<bool>(type: "boolean", nullable: false),
            prompt_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            prompt_update_id = table.Column<int>(type: "integer", nullable: false),
            failed_attempts = table.Column<int>(type: "integer", nullable: false),
            attempt_window_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_tester_mode_users", row => row.user_id));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("tester_mode_users");
}
