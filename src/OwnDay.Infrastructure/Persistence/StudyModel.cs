using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

internal static class StudyModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StudySchedule>(entity =>
        {
            entity.ToTable("learning_study_schedules", table =>
            {
                table.HasCheckConstraint("CK_learning_study_frequency", "\"EveryDays\" BETWEEN 1 AND 30");
                table.HasCheckConstraint("CK_learning_study_owner", "\"UserId\" > 0");
                table.HasCheckConstraint("CK_learning_study_invitation",
                    "(\"InvitationId\" IS NULL AND \"ProgramId\" IS NULL AND \"SkillId\" IS NULL) OR " +
                    "(\"InvitationId\" IS NOT NULL AND ((\"ProgramId\" IS NOT NULL AND \"SkillId\" IS NULL) OR " +
                    "(\"ProgramId\" IS NULL AND \"SkillId\" IS NOT NULL)))");
            });
            entity.HasKey(s => s.UserId);
            entity.Property(s => s.UserId).HasConversion(id => id.Value, value => new UserId(value)).ValueGeneratedNever();
            entity.Property(s => s.TimeZoneId).HasMaxLength(100);
            entity.Property(s => s.Windows).HasColumnType("jsonb")
                .HasConversion(w => JsonSerializer.Serialize(w, (JsonSerializerOptions?)null),
                    json => JsonSerializer.Deserialize<StudyWindow[]>(json, (JsonSerializerOptions?)null)!)
                .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<StudyWindow>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    value => value.Aggregate(0, (hash, window) => HashCode.Combine(hash, window)),
                    value => value.ToArray()));
            entity.Property(s => s.NextAt).HasConversion(value => value.UtcDateTime,
                value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
            entity.HasIndex(s => new { s.Paused, s.NextAt, s.UserId });
            entity.HasOne<LearningProgram>().WithMany().HasForeignKey(s => new { s.ProgramId, s.UserId })
                .HasPrincipalKey(p => new { p.Id, p.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Skill>().WithMany().HasForeignKey(s => new { s.SkillId, s.UserId })
                .HasPrincipalKey(s => new { s.Id, s.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
