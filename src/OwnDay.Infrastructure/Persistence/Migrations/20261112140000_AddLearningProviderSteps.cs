using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261112140000_AddLearningProviderSteps")]
public sealed class AddLearningProviderSteps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("learning_provider_steps", columns: table => new
        {
            request_id = table.Column<Guid>(type: "uuid", nullable: false),
            ordinal = table.Column<int>(type: "integer", nullable: false),
            kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
            version = table.Column<int>(type: "integer", nullable: false),
            input = table.Column<string>(type: "text", nullable: false),
            fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            target_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            targets = table.Column<string>(type: "text", nullable: false),
            result = table.Column<string>(type: "text", nullable: true),
            failure = table.Column<int>(type: "integer", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_learning_provider_steps", row => new { row.request_id, row.ordinal });
            table.ForeignKey("FK_learning_provider_steps_learning_requests_request_id", row => row.request_id,
                "learning_requests", "id", onDelete: ReferentialAction.Restrict);
        });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("learning_provider_steps");
}
