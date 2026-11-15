using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261114130000_AddProgramSuspension")]
public sealed class AddProgramSuspension : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("SuspendedFrom", "learning_lessons", type: "integer", nullable: true);
        migrationBuilder.AddColumn<Guid>("SuspendedForProgramId", "learning_lessons", type: "uuid", nullable: true);
        migrationBuilder.AddCheckConstraint("CK_learning_lessons_suspension", "learning_lessons",
            "(\"Status\" = 4 AND \"SuspendedFrom\" IN (0, 1) AND \"SuspendedFrom\" IS NOT NULL AND \"SuspendedForProgramId\" IS NOT NULL) OR (\"Status\" <> 4 AND \"SuspendedFrom\" IS NULL AND \"SuspendedForProgramId\" IS NULL)");
        migrationBuilder.CreateIndex("IX_learning_lessons_UserId_SuspendedForProgramId", "learning_lessons",
            ["UserId", "SuspendedForProgramId"], unique: true, filter: "\"Status\" = 4");
        migrationBuilder.CreateIndex("IX_learning_lessons_SuspendedForProgramId_UserId", "learning_lessons", ["SuspendedForProgramId", "UserId"]);
        migrationBuilder.AddForeignKey("FK_learning_lessons_suspended_program", "learning_lessons",
            ["SuspendedForProgramId", "UserId"], "learning_programs", principalColumns: ["Id", "UserId"], onDelete: ReferentialAction.Restrict);
        migrationBuilder.AlterColumn<int>("schema_version", "dialogue_contexts", type: "integer", nullable: false,
            defaultValue: 5, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 4);
        migrationBuilder.Sql("UPDATE dialogue_contexts SET schema_version = 5 WHERE schema_version = 4");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_lessons WHERE "SuspendedForProgramId" IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM dialogue_contexts WHERE (pending_confirmation->>'operation')::int IN (23, 24, 25, 26)) THEN
                    RAISE EXCEPTION 'Retain program return points and switch consent; roll back to a compatible reader';
                END IF;
            END $$;
            UPDATE dialogue_contexts SET schema_version = 4 WHERE schema_version = 5;
            """);
        migrationBuilder.AlterColumn<int>("schema_version", "dialogue_contexts", type: "integer", nullable: false,
            defaultValue: 4, oldClrType: typeof(int), oldType: "integer", oldDefaultValue: 5);
        migrationBuilder.DropForeignKey("FK_learning_lessons_suspended_program", "learning_lessons");
        migrationBuilder.DropIndex("IX_learning_lessons_UserId_SuspendedForProgramId", "learning_lessons");
        migrationBuilder.DropIndex("IX_learning_lessons_SuspendedForProgramId_UserId", "learning_lessons");
        migrationBuilder.DropCheckConstraint("CK_learning_lessons_suspension", "learning_lessons");
        migrationBuilder.DropColumn("SuspendedFrom", "learning_lessons");
        migrationBuilder.DropColumn("SuspendedForProgramId", "learning_lessons");
    }
}
