using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("202611050001_AddLearningLessons")]
public sealed partial class AddLearningLessons : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("learning_lessons", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            UserId = table.Column<long>(type: "bigint", nullable: false),
            Topic = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
            Theory = table.Column<string>(type: "character varying(2500)", maxLength: 2500, nullable: false),
            Question = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
            Answer = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: true),
            Feedback = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: true),
            IsCorrect = table.Column<bool>(type: "boolean", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_learning_lessons", x => x.Id));
        migrationBuilder.CreateIndex("IX_learning_lessons_UserId", "learning_lessons", "UserId", unique: true, filter: "\"Answer\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("learning_lessons");
}
