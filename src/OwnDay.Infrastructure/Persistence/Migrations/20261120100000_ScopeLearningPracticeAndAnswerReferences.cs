using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OwnDay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OwnDayDbContext))]
[Migration("20261120100000_ScopeLearningPracticeAndAnswerReferences")]
public sealed class ScopeLearningPracticeAndAnswerReferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("PracticeProgramId", "learning_lessons", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<int>("ReferenceKind", "learning_lessons", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.Sql(
            """
            UPDATE learning_lessons
            SET "ReferenceKind" = 1
            WHERE "ExpectedAnswer" IS NOT NULL AND (
                "Provenance" ->> 'Generator' LIKE 'OpenAI:%'
                OR "Content" ->> 'Id' = 'generated-exercise'
                OR "Content" -> 'StructuredRubric' ->> 'Version' = '2'
            );

            UPDATE learning_provider_steps AS step
            SET result = jsonb_set(step.result::jsonb, '{referenceKind}', '1'::jsonb)::text
            FROM learning_requests AS request
            WHERE request.id = step.request_id AND request.status IN (1, 2)
                AND step.kind = 'generate' AND step.result::jsonb ->> 'expectedAnswer' IS NOT NULL
                AND NOT (step.result::jsonb ? 'referenceKind') AND (
                step.result::jsonb -> 'provenance' ->> 'generator' LIKE 'OpenAI:%'
                OR step.result::jsonb -> 'content' ->> 'id' = 'generated-exercise'
                OR step.result::jsonb -> 'content' -> 'structuredRubric' ->> 'version' = '2'
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ReferenceKind", "learning_lessons");
        migrationBuilder.DropColumn("PracticeProgramId", "learning_lessons");
    }
}
