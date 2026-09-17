using Microsoft.EntityFrameworkCore;

namespace OwnDay.Infrastructure.Persistence;

public sealed class OwnDayDbContext(DbContextOptions<OwnDayDbContext> options)
    : DbContext(options)
{
    public DbSet<ProcessedTelegramUpdate> ProcessedTelegramUpdates =>
        Set<ProcessedTelegramUpdate>();

    public DbSet<TelegramOutboxMessage> TelegramOutboxMessages =>
        Set<TelegramOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedTelegramUpdate>(entity =>
        {
            entity.ToTable("processed_telegram_updates");
            entity.HasKey(update => update.UpdateId);
            entity.Property(update => update.UpdateId)
                .ValueGeneratedNever()
                .HasColumnName("update_id");
            entity.Property(update => update.ReceivedAt).HasColumnName("received_at");
            entity.Property(update => update.ProcessedAt).HasColumnName("processed_at");
        });

        modelBuilder.Entity<TelegramOutboxMessage>(entity =>
        {
            entity.ToTable("telegram_outbox_messages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Id).HasColumnName("id");
            entity.Property(message => message.ChatId).HasColumnName("chat_id");
            entity.Property(message => message.Text).HasColumnName("text");
            entity.Property(message => message.Status).HasColumnName("status");
            entity.Property(message => message.AttemptCount).HasColumnName("attempt_count");
            entity.Property(message => message.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(message => message.CreatedAt).HasColumnName("created_at");
            entity.Property(message => message.SentAt).HasColumnName("sent_at");
            entity.Property(message => message.LastError).HasColumnName("last_error");
            entity.HasIndex(message => new { message.Status, message.NextAttemptAt });
            entity.HasIndex(message => new { message.Status, message.SentAt });
        });
    }
}
