using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddInboxAnalysisParameters : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "max_output_tokens",
            table: "inbox_analyses",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "reasoning_effort",
            table: "inbox_analyses",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "timeout_seconds",
            table: "inbox_analyses",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "max_output_tokens",
            table: "inbox_analyses");

        migrationBuilder.DropColumn(
            name: "reasoning_effort",
            table: "inbox_analyses");

        migrationBuilder.DropColumn(
            name: "timeout_seconds",
            table: "inbox_analyses");
    }
}
