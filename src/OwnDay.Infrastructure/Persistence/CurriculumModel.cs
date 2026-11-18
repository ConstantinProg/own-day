using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;

namespace OwnDay.Infrastructure.Persistence;

internal static class CurriculumModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LearningContext>(entity =>
        {
            entity.Property(context => context.Practice).HasConversion(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
                value => JsonSerializer.Deserialize<LearningPractice>(value, JsonSerializerOptions.Web)).HasColumnType("jsonb");
            entity.ToTable("learning_contexts");
            entity.HasKey(c => c.UserId);
            entity.Property(c => c.UserId).HasConversion(id => id.Value, value => new UserId(value)).ValueGeneratedNever();
            entity.HasOne<LearningProgram>().WithMany().HasForeignKey(c => new { c.ProgramId, c.UserId })
                .HasPrincipalKey(p => new { p.Id, p.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProgramSkillLink>(entity =>
        {
            entity.ToTable("learning_program_skill_links");
            entity.HasKey(l => new { l.ProgramId, l.ProgressVersion, l.TopicCode });
            entity.Property(l => l.UserId).HasConversion(id => id.Value, value => new UserId(value));
            entity.Property(l => l.TopicCode).HasMaxLength(CurriculumTopic.MaxCodeLength);
            entity.Property(l => l.Reason).HasMaxLength(ProgramSkillLink.MaxReasonLength);
            entity.Property(l => l.Source).HasMaxLength(50);
            entity.Property(l => l.Trust).HasMaxLength(50);
            entity.HasOne<LearningProgram>().WithMany().HasForeignKey(l => new { l.ProgramId, l.UserId })
                .HasPrincipalKey(p => new { p.Id, p.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CurriculumRoute>().WithMany().HasForeignKey(l => new { l.ProgramId, l.ProgressVersion })
                .HasPrincipalKey(r => new { r.ProgramId, r.Version }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Skill>().WithMany().HasForeignKey(l => new { l.SkillId, l.UserId })
                .HasPrincipalKey(s => new { s.Id, s.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LearningProgram>(entity =>
        {
            entity.ToTable("learning_programs");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.UserId).HasConversion(id => id.Value, value => new UserId(value));
            entity.HasAlternateKey(p => new { p.Id, p.UserId });
            entity.HasIndex(p => p.UserId);
            entity.Property(p => p.Goal).HasMaxLength(LearningProgram.MaxGoalLength);
            entity.Property(p => p.GoalCriterion).HasMaxLength(LearningProgram.MaxGoalCriterionLength);
            entity.Property(p => p.Experience).HasMaxLength(LearningProgram.MaxExperienceLength);
            entity.Property(p => p.Discipline).HasMaxLength(LearningProgram.MaxDisciplineLength);
            entity.Property(p => p.DiagnosticQuestion).HasMaxLength(LearningProgram.MaxDiagnosticQuestionLength);
            entity.Property(p => p.DiagnosticAnswer).HasMaxLength(LearningProgram.MaxDiagnosticAnswerLength);
            entity.Property(p => p.EnglishDiagnostic).HasConversion(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
                value => JsonSerializer.Deserialize<EnglishDiagnostic>(value, JsonSerializerOptions.Web)).HasColumnType("jsonb");
            entity.Ignore(p => p.CurrentRoute);
            entity.HasOne<Project>().WithMany().HasForeignKey(p => new { p.ProjectId, p.UserId })
                .HasPrincipalKey(p => new { p.Id, p.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(p => p.Routes).WithOne().HasForeignKey(r => r.ProgramId).OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(p => p.Routes).HasField("_routes").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<CurriculumRoute>(entity =>
        {
            entity.ToTable("learning_routes");
            entity.Ignore(r => r.ProgressRootVersion);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.HasAlternateKey(r => new { r.ProgramId, r.Version });
            entity.Property(r => r.Reason).HasMaxLength(CurriculumRoute.MaxReasonLength);
            entity.Property(r => r.Topics).HasConversion(value => Serialize(value), value => Deserialize(value))
                .HasColumnType("jsonb").Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<CurriculumTopic>>(
                    (left, right) => Serialize(left!) == Serialize(right!), value => Serialize(value).GetHashCode(), value => value));
        });
        modelBuilder.Entity<Lesson>(entity =>
        {
            entity.ToTable("learning_lessons", table => table.HasCheckConstraint("CK_learning_lessons_suspension",
                "(\"Status\" = 4 AND \"SuspendedFrom\" IN (0, 1) AND \"SuspendedFrom\" IS NOT NULL AND \"SuspendedForProgramId\" IS NOT NULL) OR (\"Status\" <> 4 AND \"SuspendedFrom\" IS NULL AND \"SuspendedForProgramId\" IS NULL)"));
            entity.HasIndex(lesson => new { lesson.UserId, lesson.SuspendedForProgramId }).IsUnique().HasFilter("\"Status\" = 4");
            entity.HasOne<LearningProgram>().WithMany().HasForeignKey(lesson => new { lesson.SuspendedForProgramId, lesson.UserId })
                .HasPrincipalKey(program => new { program.Id, program.UserId }).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_learning_lessons_suspended_program");
            entity.ToTable("learning_lessons", table => table.HasCheckConstraint(
                "CK_learning_lessons_project_action", "\"ProjectActionId\" IS NULL OR \"ProgramId\" IS NOT NULL"));
            entity.HasOne<OwnDay.Domain.Actions.Action>().WithMany().HasForeignKey(l => new { l.ProjectActionId, l.UserId })
                .HasPrincipalKey(a => new { a.Id, a.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.Property(l => l.TopicCode).HasMaxLength(CurriculumTopic.MaxCodeLength);
            entity.HasOne<LearningProgram>().WithMany().HasForeignKey(l => new { l.ProgramId, l.UserId })
                .HasPrincipalKey(p => new { p.Id, p.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CurriculumRoute>().WithMany().HasForeignKey(l => new { l.ProgramId, l.RouteVersion })
                .HasPrincipalKey(r => new { r.ProgramId, r.Version }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static string Serialize(IReadOnlyList<CurriculumTopic> topics) => JsonSerializer.Serialize(topics);
    private static IReadOnlyList<CurriculumTopic> Deserialize(string json) => Array.AsReadOnly(JsonSerializer.Deserialize<CurriculumTopic[]>(json)!);
}
