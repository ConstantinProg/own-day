using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261118210000_AddStudyReminderConsent")]
public sealed class AddStudyReminderConsent : Migration
{
    private const string Table = "learning_study_schedules";
    private const string InvitationConstraint = "CK_learning_study_invitation";
    private const string ConsentConstraint = "CK_learning_study_consent";
    private const string ExerciseForeignKey = "FK_learning_study_open_exercise";
    private const string ExerciseIndex = "IX_learning_study_schedules_LessonId_UserId";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("LessonId", Table, type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("QuestionFingerprint", Table, type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<int>("ReminderTypes", Table, type: "integer", nullable: true);
        migrationBuilder.DropCheckConstraint(InvitationConstraint, Table);
        migrationBuilder.AddCheckConstraint(InvitationConstraint, Table, """
            (CASE WHEN "ProgramId" IS NULL THEN 0 ELSE 1 END + CASE WHEN "SkillId" IS NULL THEN 0 ELSE 1 END + CASE WHEN "LessonId" IS NULL THEN 0 ELSE 1 END) = (CASE WHEN "InvitationId" IS NULL THEN 0 ELSE 1 END) AND ("LessonId" IS NULL) = ("QuestionFingerprint" IS NULL)
            """);
        migrationBuilder.AddCheckConstraint(ConsentConstraint, Table, """
            "ReminderTypes" IS NULL OR "ReminderTypes" BETWEEN 1 AND 7 OR ("ReminderTypes" = 0 AND "Paused")
            """);
        migrationBuilder.CreateIndex(ExerciseIndex, Table, ["LessonId", "UserId"]);
        migrationBuilder.AddForeignKey(ExerciseForeignKey, Table, ["LessonId", "UserId"], "learning_lessons",
            principalColumns: ["Id", "UserId"], onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM learning_study_schedules WHERE "ReminderTypes" IS NOT NULL OR "LessonId" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Retain reminder consent; roll back to a compatible reader';
                END IF;
            END $$;
            """);
        migrationBuilder.DropForeignKey(ExerciseForeignKey, Table);
        migrationBuilder.DropIndex(ExerciseIndex, Table);
        migrationBuilder.DropCheckConstraint(InvitationConstraint, Table);
        migrationBuilder.DropCheckConstraint(ConsentConstraint, Table);
        migrationBuilder.DropColumn("LessonId", Table);
        migrationBuilder.DropColumn("QuestionFingerprint", Table);
        migrationBuilder.DropColumn("ReminderTypes", Table);
        migrationBuilder.AddCheckConstraint(InvitationConstraint, Table, """
            ("InvitationId" IS NULL AND "ProgramId" IS NULL AND "SkillId" IS NULL) OR ("InvitationId" IS NOT NULL AND (("ProgramId" IS NOT NULL AND "SkillId" IS NULL) OR ("ProgramId" IS NULL AND "SkillId" IS NOT NULL)))
            """);
    }
}
