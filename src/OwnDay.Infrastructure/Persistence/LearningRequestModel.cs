using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;

namespace OwnDay.Infrastructure.Persistence;

internal static class LearningRequestModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LearningRequest>(entity =>
        {
            entity.ToTable("learning_requests", table =>
            {
                table.HasCheckConstraint("CK_learning_requests_identity", "user_id > 0 AND sequence > 0 AND payload_version > 0");
                table.HasCheckConstraint("CK_learning_requests_completion",
                    "(status = 0 AND completed_at IS NOT NULL AND completed_at >= received_at AND outcome IS NOT NULL AND lease_token IS NULL AND lease_until IS NULL) OR " +
                    "(status = 1 AND completed_at IS NULL AND outcome IS NULL AND lease_token IS NULL AND lease_until IS NULL) OR " +
                    "(status = 2 AND completed_at IS NULL AND outcome IS NULL AND lease_token IS NOT NULL AND lease_until IS NOT NULL)");
                table.HasCheckConstraint("CK_learning_requests_attempts", "attempt_count >= 0");
            });
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Id).ValueGeneratedNever().HasColumnName("id");
            entity.Property(request => request.UserId).HasConversion(id => id.Value, value => new UserId(value)).HasColumnName("user_id");
            entity.Property(request => request.Sequence).HasColumnName("sequence");
            entity.Property(request => request.SourceKey).HasMaxLength(100).HasColumnName("source_key");
            entity.Property(request => request.SourceUpdateId).HasColumnName("source_update_id");
            entity.Property(request => request.SourceMessageId).HasColumnName("source_message_id");
            entity.Property(request => request.ChatId).HasColumnName("chat_id");
            entity.Property(request => request.PayloadKind).HasMaxLength(50).HasColumnName("payload_kind");
            entity.Property(request => request.PayloadVersion).HasColumnName("payload_version");
            entity.Property(request => request.NormalizedInput).HasColumnType("jsonb").HasColumnName("normalized_input");
            entity.Property(request => request.Status).HasColumnName("status");
            entity.Property(request => request.Outcome).HasMaxLength(100).HasColumnName("outcome");
            entity.Property(request => request.ReceivedAt).HasColumnName("received_at");
            entity.Property(request => request.CompletedAt).HasColumnName("completed_at");
            entity.Property(request => request.AvailableAt).HasColumnName("available_at");
            entity.Property(request => request.AttemptCount).HasColumnName("attempt_count");
            entity.Property(request => request.LeaseToken).HasColumnName("lease_token");
            entity.Property(request => request.LeaseUntil).HasColumnName("lease_until");
            entity.HasIndex(request => request.SourceKey).IsUnique();
            entity.HasIndex(request => new { request.UserId, request.Sequence }).IsUnique();
            entity.HasIndex(request => new { request.ReceivedAt, request.UserId, request.Sequence })
                .HasFilter("status IN (1, 2)").HasDatabaseName("IX_learning_requests_active_head");
        });
        modelBuilder.Entity<LearningAppliedOperation>(entity =>
        {
            entity.ToTable("learning_applied_operations");
            entity.HasKey(operation => new { operation.RequestId, operation.Step });
            entity.Property(operation => operation.RequestId).HasColumnName("request_id");
            entity.Property(operation => operation.Step).HasMaxLength(50).HasColumnName("step");
            entity.Property(operation => operation.AppliedAt).HasColumnName("applied_at");
            entity.HasOne<LearningRequest>().WithMany().HasForeignKey(operation => operation.RequestId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LearningProviderStep>(entity =>
        {
            entity.ToTable("learning_provider_steps", table =>
            {
                table.HasCheckConstraint("CK_learning_provider_steps_identity", "ordinal BETWEEN 0 AND 1 AND version > 0");
                table.HasCheckConstraint("CK_learning_provider_steps_result", "result IS NULL OR failure IS NULL");
            });
            entity.HasKey(step => new { step.RequestId, step.Ordinal });
            entity.Property(step => step.RequestId).HasColumnName("request_id");
            entity.Property(step => step.Ordinal).HasColumnName("ordinal");
            entity.Property(step => step.Kind).HasMaxLength(20).HasColumnName("kind");
            entity.Property(step => step.Version).HasColumnName("version");
            entity.Property(step => step.Input).HasColumnName("input");
            entity.Property(step => step.Fingerprint).HasMaxLength(64).HasColumnName("fingerprint");
            entity.Property(step => step.TargetStamp).HasMaxLength(64).HasColumnName("target_stamp");
            entity.Property(step => step.Targets).HasColumnName("targets");
            entity.Property(step => step.Result).HasColumnName("result");
            entity.Property(step => step.Failure).HasColumnName("failure");
            entity.HasOne<LearningRequest>().WithMany().HasForeignKey(step => step.RequestId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LearningProviderAttempt>(entity =>
        {
            entity.ToTable("learning_provider_attempts", table => table.HasCheckConstraint("CK_learning_provider_attempts_state",
                "number BETWEEN 1 AND 3 AND ((finished_at IS NULL AND outcome IS NULL) OR (finished_at IS NOT NULL AND outcome IS NOT NULL))"));
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.Id).ValueGeneratedNever().HasColumnName("id");
            entity.Property(attempt => attempt.RequestId).HasColumnName("request_id");
            entity.Property(attempt => attempt.Ordinal).HasColumnName("ordinal");
            entity.Property(attempt => attempt.Number).HasColumnName("number");
            entity.Property(attempt => attempt.LeaseToken).HasColumnName("lease_token");
            entity.Property(attempt => attempt.StartedAt).HasColumnName("started_at");
            entity.Property(attempt => attempt.FinishedAt).HasColumnName("finished_at");
            entity.Property(attempt => attempt.Outcome).HasMaxLength(50).HasColumnName("outcome");
            entity.HasIndex(attempt => new { attempt.RequestId, attempt.Ordinal, attempt.Number }).IsUnique();
            entity.HasOne<LearningProviderStep>().WithMany().HasForeignKey(attempt => new { attempt.RequestId, attempt.Ordinal }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
