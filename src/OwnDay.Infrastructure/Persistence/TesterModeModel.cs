using Microsoft.EntityFrameworkCore;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;

namespace OwnDay.Infrastructure.Persistence;

internal static class TesterModeModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TesterModeState>(entity =>
        {
            entity.ToTable("tester_mode_users");
            entity.HasKey(state => state.UserId);
            entity.Property(state => state.UserId).HasConversion(id => id.Value, value => new UserId(value))
                .ValueGeneratedNever().HasColumnName("user_id");
            entity.Property(state => state.Authorized).HasColumnName("authorized");
            entity.Property(state => state.PromptExpiresAt).HasColumnName("prompt_expires_at");
            entity.Property(state => state.PromptUpdateId).HasColumnName("prompt_update_id");
            entity.Property(state => state.FailedAttempts).HasColumnName("failed_attempts");
            entity.Property(state => state.AttemptWindowEndsAt).HasColumnName("attempt_window_ends_at");
            entity.Property(state => state.ConfirmationId).HasColumnName("confirmation_id");
            entity.Property(state => state.PendingOperation).HasColumnName("pending_operation");
        });
    }
}
