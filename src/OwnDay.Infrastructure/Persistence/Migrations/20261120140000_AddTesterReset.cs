using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261120140000_AddTesterReset")]
public sealed class AddTesterReset : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("confirmation_id", "tester_mode_users", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<int>("pending_operation", "tester_mode_users", type: "integer", nullable: true);
        migrationBuilder.CreateTable("openai_budget_carryover", columns: table => new
        {
            user_id = table.Column<long>(type: "bigint", nullable: false),
            month = table.Column<DateOnly>(type: "date", nullable: false),
            call_count = table.Column<long>(type: "bigint", nullable: false),
            charged_tokens = table.Column<long>(type: "bigint", nullable: false),
            charged_usd = table.Column<decimal>(type: "numeric(20,12)", precision: 20, scale: 12, nullable: false),
            has_unknown_cost = table.Column<bool>(type: "boolean", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_openai_budget_carryover", row => new { row.user_id, row.month }));
        migrationBuilder.CreateIndex("IX_openai_budget_carryover_month", "openai_budget_carryover", "month");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("confirmation_id", "tester_mode_users");
        migrationBuilder.DropColumn("pending_operation", "tester_mode_users");
        migrationBuilder.DropTable("openai_budget_carryover");
    }
}
