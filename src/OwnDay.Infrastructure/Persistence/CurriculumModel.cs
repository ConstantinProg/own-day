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
        modelBuilder.Entity<LearningProgram>(entity =>
        {
            entity.ToTable("learning_programs");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.UserId).HasConversion(id => id.Value, value => new UserId(value));
            entity.HasAlternateKey(p => new { p.Id, p.UserId });
            entity.HasIndex(p => p.UserId).IsUnique();
            entity.Property(p => p.Goal).HasMaxLength(500);
            entity.Property(p => p.Experience).HasMaxLength(1000);
            entity.Property(p => p.Discipline).HasMaxLength(100);
            entity.Property(p => p.DiagnosticQuestion).HasMaxLength(1000);
            entity.Property(p => p.DiagnosticAnswer).HasMaxLength(3000);
            entity.Ignore(p => p.CurrentRoute);
            entity.HasOne<Project>().WithMany().HasForeignKey(p => new { p.ProjectId, p.UserId })
                .HasPrincipalKey(p => new { p.Id, p.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(p => p.Routes).WithOne().HasForeignKey(r => r.ProgramId).OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(p => p.Routes).HasField("_routes").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<CurriculumRoute>(entity =>
        {
            entity.ToTable("learning_routes");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.HasAlternateKey(r => new { r.ProgramId, r.Version });
            entity.Property(r => r.Reason).HasMaxLength(500);
            entity.Property(r => r.Topics).HasConversion(value => Serialize(value), value => Deserialize(value))
                .HasColumnType("jsonb").Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<CurriculumTopic>>(
                    (left, right) => Serialize(left!) == Serialize(right!), value => Serialize(value).GetHashCode(), value => value));
        });
        modelBuilder.Entity<Lesson>(entity =>
        {
            entity.Property(l => l.TopicCode).HasMaxLength(40);
            entity.HasOne<LearningProgram>().WithMany().HasForeignKey(l => new { l.ProgramId, l.UserId })
                .HasPrincipalKey(p => new { p.Id, p.UserId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CurriculumRoute>().WithMany().HasForeignKey(l => new { l.ProgramId, l.RouteVersion })
                .HasPrincipalKey(r => new { r.ProgramId, r.Version }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static string Serialize(IReadOnlyList<CurriculumTopic> topics) => JsonSerializer.Serialize(topics);
    private static IReadOnlyList<CurriculumTopic> Deserialize(string json) => Array.AsReadOnly(JsonSerializer.Deserialize<CurriculumTopic[]>(json)!);
}
