using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Infrastructure.Persistence;

internal static class InboxAnalysisModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxItem>().HasAlternateKey(item => new { item.Id, item.UserId });
        modelBuilder.Entity<InboxAnalysis>(entity =>
        {
            entity.ToTable("inbox_analyses", table =>
            {
                table.HasCheckConstraint("CK_inbox_analyses_version", "version > 0");
                table.HasCheckConstraint("CK_inbox_analyses_lifecycle",
                    "(status = 0 AND completed_at IS NULL) OR (status BETWEEN 1 AND 3 AND completed_at IS NOT NULL AND completed_at >= created_at)");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever().HasColumnName("id");
            entity.Property(row => row.InboxItemId).HasColumnName("inbox_item_id");
            entity.Property(row => row.UserId).HasConversion(id => id.Value, value => new UserId(value)).HasColumnName("user_id");
            entity.Property(row => row.Version).HasColumnName("version");
            entity.Property(row => row.Status).IsConcurrencyToken().HasColumnName("status");
            entity.Property(row => row.Model).HasMaxLength(200).HasColumnName("model");
            entity.Property(row => row.SchemaVersion).HasMaxLength(50).HasColumnName("schema_version");
            entity.Property(row => row.PromptVersion).HasMaxLength(50).HasColumnName("prompt_version");
            entity.Property(row => row.ContextJson).HasColumnType("jsonb").HasColumnName("context");
            entity.Property(row => row.ContextFingerprint).HasMaxLength(64).HasColumnName("context_fingerprint");
            entity.Property(row => row.InputFingerprint).HasMaxLength(64).HasColumnName("input_fingerprint");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
            entity.Property(row => row.DiagnosticsJson).HasColumnType("jsonb").HasColumnName("diagnostics");
            entity.HasIndex(row => new { row.InboxItemId, row.Version }).IsUnique();
            entity.HasIndex(row => new { row.UserId, row.InboxItemId });
            entity.HasOne<InboxItem>().WithMany().HasForeignKey(row => new { row.InboxItemId, row.UserId })
                .HasPrincipalKey(item => new { item.Id, item.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(row => row.Proposals).WithOne().HasForeignKey(row => row.AnalysisId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InboxAnalysisProposal>(entity =>
        {
            entity.ToTable("inbox_analysis_proposals", table =>
            {
                table.HasCheckConstraint("CK_inbox_analysis_proposals_position", "position >= 0 AND position < 20");
                table.HasCheckConstraint("CK_inbox_analysis_proposals_kind", "kind BETWEEN 0 AND 4");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever().HasColumnName("id");
            entity.Property(row => row.AnalysisId).HasColumnName("analysis_id");
            entity.Property(row => row.Position).HasColumnName("position");
            entity.Property(row => row.Kind).HasColumnName("kind");
            entity.Property(row => row.Title).HasMaxLength(200).HasColumnName("title");
            entity.Property(row => row.PayloadJson).HasColumnType("jsonb").HasColumnName("payload");
            entity.HasIndex(row => new { row.AnalysisId, row.Position }).IsUnique();
        });
    }
}
