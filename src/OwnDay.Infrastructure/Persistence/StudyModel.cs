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
        modelBuilder.Entity<LearningSession>(entity =>
        {
            entity.ToTable("learning_sessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Id).ValueGeneratedNever();
            entity.Property(session => session.UserId).HasConversion(id => id.Value, value => new UserId(value));
            entity.Property(session => session.Summary).HasConversion(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
                value => JsonSerializer.Deserialize<LearningSessionSummary>(value, JsonSerializerOptions.Web)).HasColumnType("jsonb");
            entity.HasIndex(session => new { session.UserId, session.StartedAt, session.FirstSourceOrder });
            entity.HasOne<LearningProgram>().WithMany().HasForeignKey(session => new { session.ProgramId, session.UserId })
                .HasPrincipalKey(program => new { program.Id, program.UserId }).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_learning_sessions_program");
        });
        modelBuilder.Entity<StudySchedule>(entity =>
        {
            entity.ToTable("learning_study_schedules", table =>
            {
                table.HasCheckConstraint("CK_learning_study_frequency", "\"EveryDays\" BETWEEN 1 AND 30");
                table.HasCheckConstraint("CK_learning_study_owner", "\"UserId\" > 0");
                table.HasCheckConstraint("CK_learning_study_invitation", """
                    (CASE WHEN "ProgramId" IS NULL THEN 0 ELSE 1 END + CASE WHEN "SkillId" IS NULL THEN 0 ELSE 1 END + CASE WHEN "LessonId" IS NULL THEN 0 ELSE 1 END) = (CASE WHEN "InvitationId" IS NULL THEN 0 ELSE 1 END) AND ("LessonId" IS NULL) = ("QuestionFingerprint" IS NULL)
                    """);
                table.HasCheckConstraint("CK_learning_study_consent", """
                    "ReminderTypes" IS NULL OR "ReminderTypes" BETWEEN 1 AND 7 OR ("ReminderTypes" = 0 AND "Paused")
                    """);
            });
            entity.HasKey(s => s.UserId);
            entity.Property(s => s.UserId).HasConversion(id => id.Value, value => new UserId(value)).ValueGeneratedNever();
            entity.Property(s => s.TimeZoneId).HasMaxLength(100);
            entity.Property(s => s.QuestionFingerprint).HasMaxLength(64);
            entity.Ignore(s => s.EffectiveReminderTypes);
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
            entity.HasOne<Lesson>().WithMany().HasForeignKey(s => new { s.LessonId, s.UserId })
                .HasPrincipalKey(l => new { l.Id, l.UserId }).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_learning_study_open_exercise");
        });
    }
}
