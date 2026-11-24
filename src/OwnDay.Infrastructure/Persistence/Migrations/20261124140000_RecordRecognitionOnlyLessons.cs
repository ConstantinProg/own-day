using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261124140000_RecordRecognitionOnlyLessons")]
public sealed class RecordRecognitionOnlyLessons : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>("RecognitionOnly", "learning_lessons", type: "boolean", nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_lessons WHERE "RecognitionOnly") THEN
                    RAISE EXCEPTION 'Recognition-only lesson credit requires a compatible reader.';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn("RecognitionOnly", "learning_lessons");
    }
}
