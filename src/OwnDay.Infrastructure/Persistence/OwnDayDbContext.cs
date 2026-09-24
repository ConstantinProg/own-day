using Microsoft.EntityFrameworkCore;
using OwnDay.Domain.Tasks;

namespace OwnDay.Infrastructure.Persistence;

public sealed class OwnDayDbContext(DbContextOptions<OwnDayDbContext> options)
    : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    public DbSet<ProcessedTelegramUpdate> ProcessedTelegramUpdates =>
        Set<ProcessedTelegramUpdate>();

    public DbSet<TelegramOutboxMessage> TelegramOutboxMessages =>
        Set<TelegramOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.ToTable("tasks", table => table.HasCheckConstraint(
                "CK_tasks_status_completed_at",
                "(status = 0 AND completed_at IS NULL) OR (status = 1 AND completed_at IS NOT NULL)"));
            entity.HasKey(task => task.Id);
            entity.Property(task => task.Id).HasColumnName("id");
            entity.Property(task => task.UserId)
                .HasConversion(userId => userId.Value, value => new UserId(value))
                .HasColumnName("user_id");
            entity.Property(task => task.Title)
                .HasMaxLength(TaskItem.MaxTitleLength)
                .HasColumnName("title");
            entity.Property(task => task.Status)
                .IsConcurrencyToken()
                .HasColumnName("status");
            entity.Property(task => task.CreatedAt).HasColumnName("created_at");
            entity.Property(task => task.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(task => new { task.UserId, task.Status, task.CreatedAt, task.Id });
        });

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
