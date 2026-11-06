using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

internal static class KnowledgeModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LearningPreference>(entity =>
        {
            entity.ToTable("learning_preferences", table =>
            {
                table.HasCheckConstraint("CK_learning_preference_owner", "\"UserId\" > 0");
                table.HasCheckConstraint("CK_learning_preference_kind", "\"Kind\" BETWEEN 0 AND 3");
                table.HasCheckConstraint("CK_learning_preference_value", "length(trim(\"Value\")) BETWEEN 1 AND 200");
            });
            entity.HasKey(p => new { p.UserId, p.Kind });
            entity.Property(p => p.UserId).HasConversion(id => id.Value, value => new UserId(value));
            entity.Property(p => p.Value).HasMaxLength(LearningPreference.MaxValueLength);
            entity.Property(p => p.Source).HasMaxLength(50);
        });
        modelBuilder.Entity<ReviewSchedule>(entity =>
        {
            entity.ToTable("learning_reviews");
            entity.HasKey(r => r.SkillId);
            entity.Property(r => r.SkillId).ValueGeneratedNever();
            entity.Property(r => r.UserId).HasConversion(id => id.Value, value => new UserId(value));
            entity.Property(r => r.DueAt).HasConversion(value => value.UtcDateTime,
                value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
            entity.HasIndex(r => new { r.UserId, r.DueAt, r.SkillId });
            entity.HasOne<Skill>().WithMany().HasForeignKey(r => new { r.SkillId, r.UserId })
                .HasPrincipalKey(s => new { s.Id, s.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
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
            entity.Property(l => l.Teaching).HasConversion(value => SerializeTeaching(value!), value => DeserializeTeaching(value))
                .HasColumnType("jsonb");
            entity.Property(l => l.Transfer).HasConversion(value => SerializeTransfer(value!), value => DeserializeTransfer(value))
                .HasColumnType("jsonb");
            entity.Property(l => l.ExpectedAnswer).HasMaxLength(500);
            entity.Property(l => l.Provenance).HasConversion(value => SerializeProvenance(value!), value => DeserializeProvenance(value))
                .HasColumnType("jsonb");
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
            entity.Property(e => e.ObservedAt).HasConversion(value => value.UtcDateTime,
                value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
            entity.HasOne<Skill>().WithMany().HasForeignKey(e => new { e.SkillId, e.UserId })
                .HasPrincipalKey(s => new { s.Id, s.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Lesson>().WithMany().HasForeignKey(e => new { e.LessonId, e.UserId })
                .HasPrincipalKey(l => new { l.Id, l.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static string SerializeProvenance(LessonProvenance value) => JsonSerializer.Serialize(value);
    private static LessonProvenance DeserializeProvenance(string json) => JsonSerializer.Deserialize<LessonProvenance>(json)!;
    private static string SerializeTeaching(LessonTeaching value) => JsonSerializer.Serialize(value);
    private static LessonTeaching DeserializeTeaching(string json) => JsonSerializer.Deserialize<LessonTeaching>(json)!;
    private static string SerializeTransfer(InterdisciplinaryPractice value) => JsonSerializer.Serialize(value);
    private static InterdisciplinaryPractice DeserializeTransfer(string json) => JsonSerializer.Deserialize<InterdisciplinaryPractice>(json)!;
}
