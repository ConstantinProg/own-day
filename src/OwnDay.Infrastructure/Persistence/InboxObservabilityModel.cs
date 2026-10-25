using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;

namespace OwnDay.Infrastructure.Persistence;

internal static class InboxObservabilityModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OpenAIRequestAttempt>(entity =>
        {
            entity.ToTable("openai_request_attempts");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Model).HasMaxLength(200);
            entity.Property(a => a.Code).HasMaxLength(50);
            entity.Property(a => a.ProviderRequestId).HasMaxLength(200);
            entity.Property(a => a.EstimatedUsd).HasPrecision(20, 12);
            entity.Property(a => a.InputUsdPerMillion).HasPrecision(20, 8);
            entity.Property(a => a.CachedInputUsdPerMillion).HasPrecision(20, 8);
            entity.Property(a => a.OutputUsdPerMillion).HasPrecision(20, 8);
            entity.HasOne<InboxAnalysis>().WithMany().HasForeignKey(a => a.AnalysisId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(a => new { a.UserId, a.StartedAt });
            entity.HasIndex(a => new { a.AnalysisId, a.StartedAt });
        });
        modelBuilder.Entity<InboxOperationEvent>(entity =>
        {
            entity.ToTable("inbox_operation_events");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Code).HasMaxLength(50);
            entity.HasIndex(a => new { a.UserId, a.At });
            entity.HasIndex(a => new { a.UserId, a.Code, a.InboxItemId });
            entity.HasIndex(a => new { a.Kind, a.TargetId }).IsUnique().HasFilter("\"Code\" = 'created'");
        });
    }
}
