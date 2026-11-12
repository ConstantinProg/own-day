using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OwnDay.Application.Actions;
using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.StructuredItems;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Commands;

namespace OwnDay.Infrastructure.Learning;

public sealed class LearningRequestRunner(IServiceScopeFactory scopes, IOptions<LearningRequestProcessingOptions> options,
    LearningProviderExecutor provider)
    : ILearningRequestRunner
{
    public async Task<bool> RunAsync(LearningRequestLease lease, int maxAttempts, CancellationToken token)
    {
        // At most planner + generator followed by apply. Each iteration disposes all
        // tracked state before any external I/O; a provider is never available in apply.
        for (var phase = 0; phase < 3; phase++)
        {
            LearningProviderStep pending;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var db = services.GetRequiredService<OwnDayDbContext>();
                var steps = await db.Set<LearningProviderStep>().AsNoTracking()
                    .Where(step => step.RequestId == lease.RequestId).OrderBy(step => step.Ordinal).ToListAsync(token);
                var session = new LearningProviderSession(steps, () => TargetStamp(db), () => TargetIdentities(db));
                var handler = CreateHandler(services, session);
                return await services.GetRequiredService<ILearningRequestStore>().ApplyAsync(lease, async (request, cancellationToken) =>
                {
                    if (request.AttemptCount > maxAttempts) return new(null, "attempts_exhausted");
                    if (request.PayloadKind != "learning_command" || request.PayloadVersion != 1) return new(null, "unsupported_payload");
                    LearningCommandInput? input;
                    try { input = JsonSerializer.Deserialize<LearningCommandInput>(request.NormalizedInput, JsonSerializerOptions.Web); }
                    catch (JsonException) { return new(null, "unsupported_payload"); }
                    if (input is null || input.Name is null || input.Arguments is null || !handler.Handles(input.Name))
                        return new(null, "unsupported_payload");
                    var replies = await handler.HandleAsync(new(input.Name, input.Arguments, request.UserId), cancellationToken);
                    session.EnsureComplete();
                    return new(null, "reply_committed") { Replies = replies };
                }, token);
            }
            catch (LearningProviderRequiredException required) { pending = required.Step; }
            catch (StaleLearningPreparationException)
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ApplyAsync(lease,
                    (_, _) => Task.FromResult(new LearningRequestExecution(null, "stale_target")
                    {
                        Replies = ["Learning context changed while the request was being processed. No result was applied. Check the current lesson or repeat the original command."]
                    }), token);
            }

            await using (var scope = scopes.CreateAsyncScope())
            {
                if (!await scope.ServiceProvider.GetRequiredService<EfLearningRequestStore>().SaveStepAsync(
                    lease, pending, TimeSpan.FromSeconds(options.Value.LeaseSeconds), token)) return false;
            }
            var execution = await provider.ExecuteAsync(lease, pending, token);
            if (execution.OwnershipLost) return false;
            if (execution.Unknown)
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ApplyAsync(lease,
                    (_, _) => Task.FromResult(new LearningRequestExecution(null, "provider_outcome_unknown")
                    {
                        Replies = ["The provider outcome is unknown. No result was applied or answer assessed. Check the current lesson before explicitly retrying the original command."]
                    }), token);
            }
            pending.Result = execution.Result;
            pending.Failure = execution.Failure;
            token.ThrowIfCancellationRequested();
            await using (var scope = scopes.CreateAsyncScope())
            {
                if (!await scope.ServiceProvider.GetRequiredService<EfLearningRequestStore>().SaveStepAsync(
                    lease, pending, TimeSpan.FromSeconds(options.Value.LeaseSeconds), token)) return false;
            }
        }
        throw new InvalidOperationException("Learning command exceeded its phase limit.");
    }

    private static TelegramLearningCommandHandler CreateHandler(IServiceProvider services, LearningProviderSession session)
    {
        var knowledge = services.GetRequiredService<KnowledgeService>();
        var lessons = services.GetRequiredService<ILessonStore>();
        var programs = services.GetRequiredService<ICurriculumStore>();
        var learning = new LearningService(lessons, session, knowledge);
        var curriculum = new CurriculumService(programs, session, lessons, session,
            services.GetRequiredService<StructuredItemService>(), knowledge, session, services.GetRequiredService<IActionStore>());
        var study = new StudyService(services.GetRequiredService<IStudyScheduleStore>(), lessons, programs,
            services.GetRequiredService<IKnowledgeStore>(), learning, curriculum, services.GetRequiredService<TimeProvider>());
        return new(learning, new(curriculum), new(knowledge, learning), new(study));
    }

    private static string TargetStamp(OwnDayDbContext db)
    {
        // Addressed original row values, including keys, route versions and preferences.
        // Repeating the use case also rechecks absence, owner and prerequisite queries.
        // Newly proposed state is never part of a stamp and never survives prepare.
        var rows = db.ChangeTracker.Entries().Where(entry => entry.State != EntityState.Added &&
                entry.Entity.GetType().Namespace?.StartsWith("OwnDay.Domain", StringComparison.Ordinal) == true)
            .Select(entry => entry.Metadata.Name + ":" + JsonSerializer.Serialize(
                entry.OriginalValues.Properties.OrderBy(property => property.Name, StringComparer.Ordinal)
                    .ToDictionary(property => property.Name, property => entry.OriginalValues[property]), JsonSerializerOptions.Web))
            .OrderBy(row => row, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
    }

    private static string TargetIdentities(OwnDayDbContext db) => JsonSerializer.Serialize(db.ChangeTracker.Entries()
        .Where(entry => entry.State != EntityState.Added && entry.Metadata.FindProperty("TargetVersion") is not null &&
            entry.Entity is not OwnDay.Domain.Learning.LearningEvidence and not OwnDay.Domain.Learning.LessonInteraction)
        .Select(entry => new
        {
            Type = entry.Metadata.Name,
            Key = entry.Metadata.FindPrimaryKey()!.Properties.ToDictionary(property => property.Name, property => entry.OriginalValues[property]),
            Version = entry.OriginalValues["TargetVersion"]
        }).OrderBy(target => target.Type, StringComparer.Ordinal).ThenBy(target => JsonSerializer.Serialize(target.Key), StringComparer.Ordinal),
        JsonSerializerOptions.Web);
}
