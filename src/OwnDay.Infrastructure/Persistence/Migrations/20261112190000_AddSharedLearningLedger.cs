using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261112190000_AddSharedLearningLedger")]
public sealed class AddSharedLearningLedger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>("AnalysisId", "openai_request_attempts", type: "uuid", nullable: true,
            oldClrType: typeof(Guid), oldType: "uuid");
        migrationBuilder.AddColumn<Guid>("LearningProviderAttemptId", "openai_request_attempts", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<decimal>("ReservedUsd", "openai_request_attempts", type: "numeric(20,12)", precision: 20, scale: 12, nullable: true);
        migrationBuilder.AddColumn<decimal>("ChargedUsd", "openai_request_attempts", type: "numeric(20,12)", precision: 20, scale: 12, nullable: true);
        migrationBuilder.AddColumn<string>("PromptVersion", "openai_request_attempts", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("SchemaVersion", "openai_request_attempts", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<int>("MaxOutputTokens", "openai_request_attempts", type: "integer", nullable: true);
        migrationBuilder.CreateIndex("IX_openai_request_attempts_LearningProviderAttemptId", "openai_request_attempts", "LearningProviderAttemptId", unique: true);
        migrationBuilder.CreateIndex("IX_openai_request_attempts_StartedAt", "openai_request_attempts", "StartedAt");
        migrationBuilder.AddForeignKey("FK_openai_attempts_learning_attempt", "openai_request_attempts",
            "LearningProviderAttemptId", "learning_provider_attempts", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddCheckConstraint("CK_openai_request_attempts_source", "openai_request_attempts",
            "(\"AnalysisId\" IS NOT NULL AND \"LearningProviderAttemptId\" IS NULL) OR (\"AnalysisId\" IS NULL AND \"LearningProviderAttemptId\" IS NOT NULL)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM openai_request_attempts WHERE "LearningProviderAttemptId" IS NOT NULL
                    OR ("ReservedUsd" IS NOT NULL AND ("FinishedAt" IS NULL OR "EstimatedUsd" IS NULL))) THEN
                    RAISE EXCEPTION 'Cannot remove shared ledger with Learning history or outstanding reservations';
                END IF;
            END $$;
            """);
        migrationBuilder.DropCheckConstraint("CK_openai_request_attempts_source", "openai_request_attempts");
        migrationBuilder.DropForeignKey("FK_openai_attempts_learning_attempt", "openai_request_attempts");
        migrationBuilder.DropIndex("IX_openai_request_attempts_LearningProviderAttemptId", "openai_request_attempts");
        migrationBuilder.DropIndex("IX_openai_request_attempts_StartedAt", "openai_request_attempts");
        foreach (var column in new[] { "LearningProviderAttemptId", "ReservedUsd", "ChargedUsd", "PromptVersion", "SchemaVersion", "MaxOutputTokens" })
            migrationBuilder.DropColumn(column, "openai_request_attempts");
        migrationBuilder.AlterColumn<Guid>("AnalysisId", "openai_request_attempts", type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
    }
}
