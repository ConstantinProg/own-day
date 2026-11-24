using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Diagnostics;

internal sealed record DiagnosticFields(string? Status = null, string? Error = null, long? DurationMs = null,
    long? WaitMs = null, string? Kind = null, int? UpdateId = null, int? MessageId = null, int? Step = null,
    string? Provider = null, string? Model = null, int? InputTokens = null, int? OutputTokens = null,
    int? MaxOutputTokens = null, string? ReasoningEffort = null, int? HttpStatus = null, int? PromptVersion = null,
    string? CancellationSource = null, int? DeadlineSeconds = null);

public sealed class DiagnosticJournal(IServiceScopeFactory scopes, IOptions<DiagnosticOptions> options,
    TesterModeOptions tester, TimeProvider clock)
{
    private static readonly AsyncLocal<Context?> Current = new();
    private readonly Channel<DiagnosticEvent> _channel = Channel.CreateBounded<DiagnosticEvent>(
        new BoundedChannelOptions(options.Value.QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long _dropped;
    private readonly Dictionary<(long Owner, Guid Generation), long> _ownerLosses = [];

    internal ChannelReader<DiagnosticEvent> Reader => _channel.Reader;
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal void CountDropped(int count = 1, long? owner = null, Guid? generation = null)
    {
        Interlocked.Add(ref _dropped, count);
        var ownerId = owner ?? Current.Value?.Owner;
        if (ownerId is null) return;
        var key = (ownerId.Value, generation ?? Current.Value?.Generation ?? Guid.Empty);
        lock (_ownerLosses)
        {
            if (_ownerLosses.TryGetValue(key, out var previous)) _ownerLosses[key] = previous + count;
            else if (_ownerLosses.Count < 4096) _ownerLosses.Add(key, count);
        }
    }

    internal long? ObservedLosses(long owner, Guid generation)
    {
        lock (_ownerLosses)
            return _ownerLosses.TryGetValue((owner, generation), out var count) ? count : _ownerLosses.Count < 4096 ? 0 : null;
    }
    internal void Complete() => _channel.Writer.TryComplete();
    internal static bool Detailed => Current.Value?.Detailed == true;
    internal static Context? Active => Current.Value;

    internal async Task<Context?> CaptureAsync(UserId owner, Guid correlation, Guid? request, int? attempt, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(300));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            if (!db.Database.IsNpgsql()) return null;
            var state = await db.Database.SqlQuery<DiagnosticAccess>($"""
                SELECT COALESCE(f."Generation", '00000000-0000-0000-0000-000000000000'::uuid) AS "Generation",
                    COALESCE(f."DetailGeneration", '00000000-0000-0000-0000-000000000000'::uuid) AS "DetailGeneration",
                    COALESCE(t.authorized, false) AS "Authorized"
                FROM (SELECT {owner.Value} AS owner_id) o
                LEFT JOIN diagnostic_fences f ON f."OwnerId" = o.owner_id
                LEFT JOIN tester_mode_users t ON t.user_id = o.owner_id
                WHERE {request}::uuid IS NULL OR EXISTS (
                    SELECT 1 FROM learning_requests r WHERE r.id = {request} AND r.user_id = o.owner_id)
                """).SingleOrDefaultAsync(deadline.Token);
            return state is null ? null : new(owner.Value, state.Generation, tester.Enabled && state.Authorized, correlation, request, attempt,
                DetailGeneration: state.DetailGeneration);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            CountDropped(owner: owner.Value);
            return null;
        }
    }

    internal static IDisposable Enter(Context? context)
    {
        var previous = Current.Value;
        Current.Value = context;
        return new RestoreContext(previous);
    }

    internal void Write(string category, string code, DiagnosticFields? fields = null, string severity = "info",
        Guid? entity = null, Func<object>? detail = null, int? attempt = null, DateTime? occurredAt = null)
    {
        if (Current.Value is not { } context) return;
        try
        {
            var detailed = context.Detailed && detail is not null;
            var json = JsonSerializer.Serialize(new { fields, detail = detailed ? detail!() : null }, JsonSerializerOptions.Web);
            if (Encoding.UTF8.GetByteCount(json) > 256 * 1024)
            {
                CountDropped();
                json = JsonSerializer.Serialize(new { fields, detailOmitted = "event_size_limit" }, JsonSerializerOptions.Web);
                detailed = false;
            }
            if (!_channel.Writer.TryWrite(new(Guid.CreateVersion7(), context.Owner, context.Generation,
                occurredAt ?? clock.GetUtcNow().UtcDateTime, category, code, severity, context.Correlation, context.Request,
                attempt ?? context.Attempt, entity, 1, detailed ? "tester" : "minimal", json, context.DetailGeneration)))
                CountDropped();
        }
        catch (Exception)
        {
            CountDropped();
        }
    }

    internal sealed record Context(long Owner, Guid Generation, bool Detailed, Guid Correlation, Guid? Request, int? Attempt,
        Guid? Invocation = null, int? Step = null, string? Purpose = null, int? PromptVersion = null, Guid DetailGeneration = default, CancellationToken WorkerStoppingToken = default);
    private sealed record DiagnosticAccess(Guid Generation, Guid DetailGeneration, bool Authorized);

    private sealed class RestoreContext(Context? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
