using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OwnDay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLearningRequestLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_learning_requests_completion",
                table: "learning_requests");

            migrationBuilder.AlterColumn<string>(
                name: "outcome",
                table: "learning_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<DateTime>(
                name: "completed_at",
                table: "learning_requests",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "learning_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "available_at",
                table: "learning_requests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                table: "learning_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "learning_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "learning_applied_operations",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    applied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_applied_operations", x => new { x.request_id, x.step });
                    table.ForeignKey(
                        name: "FK_learning_applied_operations_learning_requests_request_id",
                        column: x => x.request_id,
                        principalTable: "learning_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_learning_requests_attempts",
                table: "learning_requests",
                sql: "attempt_count >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_learning_requests_completion",
                table: "learning_requests",
                sql: "(status = 0 AND completed_at IS NOT NULL AND completed_at >= received_at AND outcome IS NOT NULL AND lease_token IS NULL AND lease_until IS NULL) OR (status = 1 AND completed_at IS NULL AND outcome IS NULL AND lease_token IS NULL AND lease_until IS NULL) OR (status = 2 AND completed_at IS NULL AND outcome IS NULL AND lease_token IS NOT NULL AND lease_until IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learning_applied_operations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_learning_requests_attempts",
                table: "learning_requests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_learning_requests_completion",
                table: "learning_requests");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "learning_requests");

            migrationBuilder.DropColumn(
                name: "available_at",
                table: "learning_requests");

            migrationBuilder.DropColumn(
                name: "lease_token",
                table: "learning_requests");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "learning_requests");

            migrationBuilder.AlterColumn<string>(
                name: "outcome",
                table: "learning_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "completed_at",
                table: "learning_requests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_learning_requests_completion",
                table: "learning_requests",
                sql: "status = 0 AND completed_at >= received_at AND outcome = 'reply_committed'");
        }
    }
}
