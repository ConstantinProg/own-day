using Microsoft.EntityFrameworkCore;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Diagnostics;

public enum DiagnosticExportMode
{
    Full,
    Recent,
    Last
}

internal sealed class DiagnosticExportJob
{
    public int UpdateId { get; init; }
    public long OwnerId { get; init; }
    public DiagnosticExportMode Mode { get; init; }
    public DateTime ReceivedAt { get; init; }
    public bool InRussian { get; init; }
}

internal sealed class DiagnosticFence
{
    public long OwnerId { get; init; }
    public Guid Generation { get; set; }
    public Guid DetailGeneration { get; set; }
}

internal sealed class DiagnosticDocument
{
    public Guid OutboxId { get; init; }
    public required byte[] Contents { get; init; }
}

internal sealed record DiagnosticEvent(Guid Id, long OwnerId, Guid Generation, DateTime OccurredAt,
    string Category, string Code, string Severity, Guid CorrelationId, Guid? RequestId,
    int? AttemptNumber, Guid? EntityId, int SchemaVersion, string Detail, string Data, Guid DetailGeneration = default);

internal static class DiagnosticModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<DiagnosticEvent>(row =>
        {
            row.ToTable("diagnostic_events");
            row.HasKey(x => x.Id);
            row.Property(x => x.Data).HasColumnType("jsonb");
            row.HasIndex(x => new { x.OwnerId, x.OccurredAt, x.Id });
            row.HasIndex(x => new { x.OwnerId, x.RequestId, x.OccurredAt, x.Id });
            row.HasIndex(x => new { x.OwnerId, x.CorrelationId });
            row.HasIndex(x => new { x.OccurredAt, x.Id });
        });
        model.Entity<DiagnosticFence>(row =>
        {
            row.ToTable("diagnostic_fences");
            row.HasKey(x => x.OwnerId);
            row.Property(x => x.OwnerId).ValueGeneratedNever();
        });
        model.Entity<DiagnosticExportJob>(row =>
        {
            row.ToTable("diagnostic_export_jobs");
            row.HasKey(x => x.UpdateId);
            row.Property(x => x.UpdateId).ValueGeneratedNever();
            row.HasIndex(x => x.OwnerId).IsUnique();
            row.HasIndex(x => new { x.ReceivedAt, x.UpdateId });
        });
        model.Entity<DiagnosticDocument>(row =>
        {
            row.ToTable("diagnostic_documents");
            row.HasKey(x => x.OutboxId);
            row.HasOne<TelegramOutboxMessage>().WithOne().HasForeignKey<DiagnosticDocument>(x => x.OutboxId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
