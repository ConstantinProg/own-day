using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations;

public partial class AddInboxUndo : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "manual_processing_only", table: "inbox_items", type: "boolean",
            nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTime>(name: "undone_at", table: "inbox_automatic_applications",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(name: "undo_state", table: "inbox_automatic_applications", type: "jsonb",
            nullable: false, defaultValue: "[]");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "manual_processing_only", table: "inbox_items");
        migrationBuilder.DropColumn(name: "undone_at", table: "inbox_automatic_applications");
        migrationBuilder.DropColumn(name: "undo_state", table: "inbox_automatic_applications");
    }
}
