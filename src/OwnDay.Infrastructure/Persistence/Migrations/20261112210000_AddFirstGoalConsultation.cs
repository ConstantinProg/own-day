using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261112210000_AddFirstGoalConsultation")]
public sealed class AddFirstGoalConsultation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("consultation", "dialogue_contexts", type: "jsonb", nullable: true);
        migrationBuilder.AlterColumn<int>("schema_version", "dialogue_contexts", type: "integer", nullable: false,
            defaultValue: 2, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 1);
        migrationBuilder.Sql("UPDATE dialogue_contexts SET schema_version = 2 WHERE schema_version = 1");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN IF EXISTS (SELECT 1 FROM dialogue_contexts WHERE consultation IS NOT NULL) THEN
                RAISE EXCEPTION 'Retain consultation drafts; roll back only to a compatible reader';
            END IF; END $$;
            UPDATE dialogue_contexts SET schema_version = 1 WHERE schema_version = 2;
            """);
        migrationBuilder.AlterColumn<int>("schema_version", "dialogue_contexts", type: "integer", nullable: false,
            defaultValue: 1, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 2);
        migrationBuilder.DropColumn("consultation", "dialogue_contexts");
    }
}
