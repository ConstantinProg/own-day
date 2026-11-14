using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261114110000_AddBoundedLearningPractice")]
public sealed class AddBoundedLearningPractice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Practice", "learning_contexts", type: "jsonb", nullable: true);
        migrationBuilder.AlterColumn<int>("schema_version", "dialogue_contexts", type: "integer", nullable: false,
            defaultValue: 4, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 3);
        migrationBuilder.Sql("UPDATE dialogue_contexts SET schema_version = 4 WHERE schema_version = 3");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN IF EXISTS (SELECT 1 FROM learning_contexts WHERE "Practice" IS NOT NULL) THEN
                RAISE EXCEPTION 'Retain bounded practice associations; roll back to a compatible reader';
            END IF; END $$;
            UPDATE dialogue_contexts SET schema_version = 3 WHERE schema_version = 4;
            """);
        migrationBuilder.AlterColumn<int>("schema_version", "dialogue_contexts", type: "integer", nullable: false,
            defaultValue: 3, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 4);
        migrationBuilder.DropColumn("Practice", "learning_contexts");
    }
}
