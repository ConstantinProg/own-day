using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

internal static class KnowledgeModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Skill>(entity =>
        {
            entity.ToTable("learning_skills");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.UserId).HasConversion(id => id.Value, value => new UserId(value));
            entity.HasAlternateKey(s => new { s.Id, s.UserId });
            entity.Property(s => s.Title).HasMaxLength(500);
            entity.Property(s => s.Description).HasMaxLength(1200);
            entity.HasIndex(s => s.UserId);
        });
        modelBuilder.Entity<Lesson>(entity =>
        {
            entity.HasAlternateKey(l => new { l.Id, l.UserId });
            entity.HasOne<Skill>().WithMany().HasForeignKey(l => new { l.SkillId, l.UserId })
                .HasPrincipalKey(s => new { s.Id, s.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LearningEvidence>(entity =>
        {
            entity.ToTable("learning_evidence");
            entity.HasKey(e => e.LessonId);
            entity.Property(e => e.LessonId).ValueGeneratedNever();
            entity.Property(e => e.UserId).HasConversion(id => id.Value, value => new UserId(value));
            entity.Property(e => e.Answer).HasMaxLength(3000);
            entity.Property(e => e.AssessmentFeedback).HasMaxLength(3000);
            entity.Property(e => e.AssessmentSource).HasMaxLength(200);
            entity.Property(e => e.Method).HasMaxLength(50);
            entity.HasOne<Skill>().WithMany().HasForeignKey(e => new { e.SkillId, e.UserId })
                .HasPrincipalKey(s => new { s.Id, s.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Lesson>().WithMany().HasForeignKey(e => new { e.LessonId, e.UserId })
                .HasPrincipalKey(l => new { l.Id, l.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
