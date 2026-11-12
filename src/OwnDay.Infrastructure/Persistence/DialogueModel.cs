using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;

namespace OwnDay.Infrastructure.Persistence;

internal static class DialogueModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DialogueContext>(entity =>
        {
            entity.ToTable("dialogue_contexts", table => table.HasCheckConstraint("CK_dialogue_contexts_identity", "user_id > 0 AND revision > 0 AND schema_version > 0"));
            entity.HasKey(context => context.UserId);
            entity.Property(context => context.UserId).HasConversion(id => id.Value, value => new UserId(value)).ValueGeneratedNever().HasColumnName("user_id");
            entity.Property(context => context.Revision).IsConcurrencyToken().HasColumnName("revision");
            entity.Property(context => context.SchemaVersion).HasDefaultValue(1).HasColumnName("schema_version");
            entity.Property(context => context.Phase).HasColumnName("phase");
            entity.Property(context => context.ExpectedResponse).HasColumnName("expected_response");
            entity.Property(context => context.PromptId).HasColumnName("prompt_id");
            entity.Property(context => context.TargetLessonId).HasColumnName("target_lesson_id");
            entity.Property(context => context.QuestionFingerprint).HasColumnName("question_fingerprint").HasMaxLength(64);
            entity.Property(context => context.PromptOpenedAt).HasColumnName("prompt_opened_at");
            entity.Property(context => context.LastActivityAt).HasColumnName("last_activity_at");
            entity.Property(context => context.InterruptedAt).HasColumnName("interrupted_at");
            entity.Property(context => context.InterruptedSourceOrder).HasColumnName("interrupted_source_order");
            entity.Property(context => context.ResponseAfterSourceOrder).HasColumnName("response_after_source_order");
            entity.Property(context => context.DeferredGoalRequestId).HasColumnName("deferred_goal_request_id");
            entity.Property(context => context.PendingClarification).HasConversion(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
                value => JsonSerializer.Deserialize<DialogueClarification>(value, JsonSerializerOptions.Web)).HasColumnType("jsonb").HasColumnName("pending_clarification");
            entity.Property(context => context.PendingConfirmation).HasConversion(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
                value => JsonSerializer.Deserialize<DialogueConfirmation>(value, JsonSerializerOptions.Web)).HasColumnType("jsonb").HasColumnName("pending_confirmation");
        });
    }
}
