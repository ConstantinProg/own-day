using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Learning.Requests;

namespace OwnDay.Infrastructure.Persistence;

internal static class InboxObservabilityModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OpenAIRequestAttempt>(entity =>
        {
            entity.ToTable("openai_request_attempts", table => table.HasCheckConstraint("CK_openai_request_attempts_source",
                "(\"AnalysisId\" IS NOT NULL AND \"LearningProviderAttemptId\" IS NULL) OR (\"AnalysisId\" IS NULL AND \"LearningProviderAttemptId\" IS NOT NULL)"));
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Model).HasMaxLength(200);
            entity.Property(a => a.Code).HasMaxLength(50);
            entity.Property(a => a.ProviderRequestId).HasMaxLength(200);
            entity.Property(a => a.EstimatedUsd).HasPrecision(20, 12);
            entity.Property(a => a.ReservedUsd).HasPrecision(20, 12);
            entity.Property(a => a.ChargedUsd).HasPrecision(20, 12);
            entity.Property(a => a.PromptVersion).HasMaxLength(100);
            entity.Property(a => a.SchemaVersion).HasMaxLength(100);
            entity.Property(a => a.InputUsdPerMillion).HasPrecision(20, 8);
            entity.Property(a => a.CachedInputUsdPerMillion).HasPrecision(20, 8);
            entity.Property(a => a.OutputUsdPerMillion).HasPrecision(20, 8);
            entity.HasOne<InboxAnalysis>().WithMany().HasForeignKey(a => a.AnalysisId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LearningProviderAttempt>().WithMany().HasForeignKey(a => a.LearningProviderAttemptId)
                .HasConstraintName("FK_openai_attempts_learning_attempt").OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(a => a.LearningProviderAttemptId).IsUnique();
            entity.HasIndex(a => a.StartedAt);
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
        modelBuilder.Entity<OpenAIBudgetCarryover>(entity =>
        {
            entity.ToTable("openai_budget_carryover");
            entity.HasKey(total => new { total.UserId, total.Month });
            entity.HasIndex(total => total.Month);
            entity.Property(total => total.UserId).HasColumnName("user_id");
            entity.Property(total => total.Month).HasColumnName("month");
            entity.Property(total => total.CallCount).HasColumnName("call_count");
            entity.Property(total => total.ChargedTokens).HasColumnName("charged_tokens");
            entity.Property(total => total.ChargedUsd).HasPrecision(20, 12).HasColumnName("charged_usd");
            entity.Property(total => total.HasUnknownCost).HasColumnName("has_unknown_cost");
        });
    }
}
