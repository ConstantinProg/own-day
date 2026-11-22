using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Diagnostics;

internal sealed class DiagnosticExportWorker(IServiceScopeFactory scopes, IOptions<DiagnosticOptions> options,
    TimeProvider clock, ILogger<DiagnosticExportWorker> logger, TesterModeOptions testerOptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cleanupAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
                if (!db.Database.IsNpgsql()) return;
                if (cleanupAt <= clock.GetUtcNow())
                {
                    await ExpireDocumentsAsync(db, stoppingToken);
                    cleanupAt = clock.GetUtcNow().AddMinutes(1);
                }
                var job = await db.Set<DiagnosticExportJob>().AsNoTracking()
                    .OrderBy(row => row.ReceivedAt).ThenBy(row => row.UpdateId).FirstOrDefaultAsync(stoppingToken);
                if (job is not null)
                {
                    var result = testerOptions.Enabled ? await BuildAsync(job, stoppingToken) : new(null, null, "access_revoked");
                    await PublishAsync(db, scope.ServiceProvider.GetRequiredService<TesterModeService>(), job, result, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Diagnostic export cycle failed ({ErrorType}).", error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), clock, stoppingToken);
        }
    }

    private async Task<DiagnosticExportResult> BuildAsync(DiagnosticExportJob job, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.ExportTimeoutSeconds));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<DiagnosticExportReader>().BuildAsync(job, deadline.Token);
        }
        catch (DiagnosticExportLimitException) { return new(null, null, "export_size_limit"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, null, "export_timeout"); }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Diagnostic snapshot failed ({ErrorType}).", error.GetType().Name);
            return new(null, null, "snapshot_unavailable");
        }
    }

    private async Task PublishAsync(OwnDayDbContext db, TesterModeService tester, DiagnosticExportJob job,
        DiagnosticExportResult result, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({job.OwnerId})", token);
        var current = await db.Set<DiagnosticExportJob>().FindAsync([job.UpdateId], token);
        if (current is null) return; // Another worker or a reset removed this job.
        db.Remove(current);
        if (result.Error != "access_revoked" && await tester.CanExportAsync(new UserId(job.OwnerId), token))
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var intent = new TelegramOutboxMessage
            {
                Id = Guid.NewGuid(), ChatId = job.OwnerId, Status = OutboxMessageStatus.Pending, CreatedAt = now, NextAttemptAt = now,
                Text = result.Error is { } error ? Message(error, job.InRussian) : "",
                DocumentName = result.Name,
                DocumentExpiresAt = result.Bytes is null ? null : now.AddMinutes(options.Value.DocumentLifetimeMinutes)
            };
            db.Add(intent);
            if (result.Bytes is { } bytes) db.Add(new DiagnosticDocument { OutboxId = intent.Id, Contents = bytes });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task ExpireDocumentsAsync(OwnDayDbContext db, CancellationToken token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expired = await db.TelegramOutboxMessages.AsNoTracking()
            .Where(row => row.DocumentName != null && row.DocumentExpiresAt <= now && row.Status == OutboxMessageStatus.Pending)
            .OrderBy(row => row.DocumentExpiresAt).ThenBy(row => row.Id).Select(row => new { row.ChatId, row.Id })
            .Take(100).ToListAsync(token);
        foreach (var row in expired)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({row.ChatId}) AS \"Value\"").SingleAsync(token))
                continue;
            await db.Set<DiagnosticDocument>().Where(document => document.OutboxId == row.Id).ExecuteDeleteAsync(token);
            await db.TelegramOutboxMessages.Where(message => message.Id == row.Id && message.Status == OutboxMessageStatus.Pending)
                .ExecuteUpdateAsync(changes => changes.SetProperty(message => message.Status, OutboxMessageStatus.Failed)
                    .SetProperty(message => message.LastError, "DiagnosticDocumentExpired"), token);
            await transaction.CommitAsync(token);
        }
    }

    private static string Message(string code, bool russian) => (code, russian) switch
    {
        ("no_requests", true) => "Пользовательских запросов пока нет.",
        ("no_requests", false) => "There are no user requests yet.",
        ("export_size_limit", true) => "Экспорт превысил допустимый размер. Попробуйте /export recent или /export last.",
        ("export_size_limit", false) => "The export exceeds the size limit. Try /export recent or /export last.",
        ("export_timeout", true) => "Время формирования экспорта истекло. Попробуйте ещё раз.",
        ("export_timeout", false) => "The export timed out. Try again.",
        (_, true) => "Не удалось прочитать диагностические данные. Попробуйте позже.",
        _ => "The diagnostic data could not be read. Try again later."
    };
}
