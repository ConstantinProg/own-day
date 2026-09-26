using Microsoft.EntityFrameworkCore;
using OwnDay.Domain;
using OwnDay.Domain.Actions;
using OwnDay.Domain.Projects;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.References;
using OwnDay.Domain.WaitingFors;
using OwnDay.Domain.Inbox;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Infrastructure.Persistence;

public sealed class OwnDayDbContext(DbContextOptions<OwnDayDbContext> options)
    : DbContext(options)
{
    public DbSet<Action> Actions => Set<Action>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<SomedayMaybe> SomedayMaybes => Set<SomedayMaybe>();
    public DbSet<Reference> References => Set<Reference>();
    public DbSet<WaitingFor> WaitingFors => Set<WaitingFor>();
    public DbSet<InboxItem> InboxItems => Set<InboxItem>();

    public DbSet<ProcessedTelegramUpdate> ProcessedTelegramUpdates =>
        Set<ProcessedTelegramUpdate>();

    public DbSet<TelegramOutboxMessage> TelegramOutboxMessages =>
        Set<TelegramOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxItem>(entity =>
        {
            entity.ToTable("inbox_items", table => table.HasCheckConstraint(
                "CK_inbox_items_lifecycle",
                "(status = 0 AND processed_at IS NULL AND discarded_at IS NULL AND target_kind IS NULL AND target_id IS NULL) OR " +
                "(status = 1 AND processed_at IS NOT NULL AND discarded_at IS NULL AND target_kind IS NOT NULL AND target_kind BETWEEN 0 AND 4 AND target_id IS NOT NULL AND target_id > 0 AND processed_at >= captured_at) OR " +
                "(status = 2 AND processed_at IS NULL AND discarded_at IS NOT NULL AND target_kind IS NULL AND target_id IS NULL AND discarded_at >= captured_at)"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.UserId).HasConversion(id => id.Value, value => new UserId(value)).HasColumnName("user_id");
            entity.Property(item => item.OriginalText).HasColumnType("text").HasColumnName("original_text");
            entity.Property(item => item.CapturedAt).HasColumnName("captured_at");
            entity.Property(item => item.Status).IsConcurrencyToken().HasColumnName("status");
            entity.Property(item => item.ProcessedAt).HasColumnName("processed_at");
            entity.Property(item => item.DiscardedAt).HasColumnName("discarded_at");
            entity.Property(item => item.TargetKind).HasColumnName("target_kind");
            entity.Property(item => item.TargetId).HasColumnName("target_id");
            entity.HasIndex(item => new { item.UserId, item.Status, item.CapturedAt, item.Id });
        });

        modelBuilder.Entity<Action>(entity =>
        {
            entity.ToTable("actions", table => table.HasCheckConstraint(
                "CK_actions_status_completed_at",
                "(status = 0 AND completed_at IS NULL) OR (status = 1 AND completed_at IS NOT NULL)"));
            entity.HasKey(action => action.Id);
            entity.Property(action => action.Id).HasColumnName("id");
            entity.Property(action => action.UserId)
                .HasConversion(userId => userId.Value, value => new UserId(value))
                .HasColumnName("user_id");
            entity.Property(action => action.Title)
                .HasMaxLength(Action.MaxTitleLength)
                .HasColumnName("title");
            entity.Property(action => action.Status)
                .IsConcurrencyToken()
                .HasColumnName("status");
            entity.Property(action => action.CreatedAt).HasColumnName("created_at");
            entity.Property(action => action.CompletedAt).HasColumnName("completed_at");
            entity.Property(action => action.ProjectId).HasColumnName("project_id");
            entity.HasOne<Project>().WithMany()
                .HasForeignKey(action => new { action.ProjectId, action.UserId })
                .HasPrincipalKey(project => new { project.Id, project.UserId });
            entity.HasIndex(action => new { action.UserId, action.Status, action.CreatedAt, action.Id });
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects", table => table.HasCheckConstraint(
                "CK_projects_status_completed_at",
                "(status = 0 AND completed_at IS NULL) OR (status = 1 AND completed_at IS NOT NULL)"));
            entity.HasKey(project => project.Id);
            entity.HasAlternateKey(project => new { project.Id, project.UserId });
            entity.Property(project => project.Id).HasColumnName("id");
            entity.Property(project => project.UserId).HasConversion(id => id.Value, value => new UserId(value)).HasColumnName("user_id");
            entity.Property(project => project.Title).HasMaxLength(Project.MaxTitleLength).HasColumnName("title");
            entity.Property(project => project.Status).IsConcurrencyToken().HasColumnName("status");
            entity.Property(project => project.CreatedAt).HasColumnName("created_at");
            entity.Property(project => project.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(project => new { project.UserId, project.Status, project.CreatedAt, project.Id });
        });

        modelBuilder.Entity<SomedayMaybe>(entity =>
        {
            entity.ToTable("someday_maybes", table => table.HasCheckConstraint(
                "CK_someday_maybes_status_archived_at",
                "(status = 0 AND archived_at IS NULL) OR (status = 1 AND archived_at IS NOT NULL)"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.UserId).HasConversion(id => id.Value, value => new UserId(value)).HasColumnName("user_id");
            entity.Property(item => item.Text).HasMaxLength(SomedayMaybe.MaxTextLength).HasColumnName("text");
            entity.Property(item => item.Status).IsConcurrencyToken().HasColumnName("status");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.ArchivedAt).HasColumnName("archived_at");
            entity.Property(item => item.ProjectId).HasColumnName("project_id");
            entity.HasOne<Project>().WithMany().HasForeignKey(item => new { item.ProjectId, item.UserId })
                .HasPrincipalKey(project => new { project.Id, project.UserId });
            entity.HasIndex(item => new { item.UserId, item.Status, item.CreatedAt, item.Id });
        });

        modelBuilder.Entity<Reference>(entity =>
        {
            entity.ToTable("references", table => table.HasCheckConstraint(
                "CK_references_status_archived_at",
                "(status = 0 AND archived_at IS NULL) OR (status = 1 AND archived_at IS NOT NULL)"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.UserId).HasConversion(id => id.Value, value => new UserId(value)).HasColumnName("user_id");
            entity.Property(item => item.Text).HasMaxLength(Reference.MaxTextLength).HasColumnName("text");
            entity.Property(item => item.Status).IsConcurrencyToken().HasColumnName("status");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.ArchivedAt).HasColumnName("archived_at");
            entity.Property(item => item.ProjectId).HasColumnName("project_id");
            entity.HasOne<Project>().WithMany().HasForeignKey(item => new { item.ProjectId, item.UserId })
                .HasPrincipalKey(project => new { project.Id, project.UserId });
            entity.HasIndex(item => new { item.UserId, item.Status, item.CreatedAt, item.Id });
        });

        modelBuilder.Entity<WaitingFor>(entity =>
        {
            entity.ToTable("waiting_fors", table => table.HasCheckConstraint(
                "CK_waiting_fors_status_resolved_at",
                "(status = 0 AND resolved_at IS NULL) OR (status = 1 AND resolved_at IS NOT NULL)"));
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id");
            entity.Property(item => item.UserId).HasConversion(id => id.Value, value => new UserId(value)).HasColumnName("user_id");
            entity.Property(item => item.Description).HasMaxLength(WaitingFor.MaxDescriptionLength).HasColumnName("description");
            entity.Property(item => item.Source).HasMaxLength(WaitingFor.MaxSourceLength).HasColumnName("source");
            entity.Property(item => item.WaitingSince).HasColumnName("waiting_since");
            entity.Property(item => item.Status).IsConcurrencyToken().HasColumnName("status");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(item => item.ProjectId).HasColumnName("project_id");
            entity.HasOne<Project>().WithMany().HasForeignKey(item => new { item.ProjectId, item.UserId })
                .HasPrincipalKey(project => new { project.Id, project.UserId });
            entity.HasIndex(item => new { item.UserId, item.Status, item.CreatedAt, item.Id });
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
