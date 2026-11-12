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
                table.HasCheckConstraint("CK_learning_requests_completion", "status = 0 AND completed_at >= received_at AND outcome = 'reply_committed'");
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
            entity.HasIndex(request => request.SourceKey).IsUnique();
            entity.HasIndex(request => new { request.UserId, request.Sequence }).IsUnique();
        });
    }
}
