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

public sealed class LearningRequestRunner(IServiceScopeFactory scopes, IOptions<LearningRequestProcessingOptions> options)
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
            // This scope contains only the adapter's services. No entity/DbContext
            // from prepare is retained, and no dedicated DB connection is open.
            await using (var scope = scopes.CreateAsyncScope())
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds));
                try { pending.Result = await ExecuteProviderAsync(scope.ServiceProvider, pending, timeout.Token); }
                catch (LessonProviderException failure) { pending.Failure = failure.Reason; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    pending.Failure = LessonProviderFailure.Timeout;
                }
            }
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

    private static async Task<string> ExecuteProviderAsync(IServiceProvider services, LearningProviderStep step, CancellationToken token)
    {
        object result;
        switch (step.Kind)
        {
            case "generate":
                result = await services.GetRequiredService<ILessonTeacher>().GenerateAsync(step.ReadInput<LessonRequest>(), token);
                break;
            case "assess":
                var assessment = step.ReadInput<LessonAssessmentInput>();
                result = await services.GetRequiredService<ILessonTeacher>().AssessAsync(assessment.Lesson, assessment.Answer, token);
                break;
            case "explain":
                var explanation = step.ReadInput<LessonExplanationInput>();
                result = await services.GetRequiredService<ILessonTeacher>().ExplainAsync(explanation.Lesson, explanation.Request, explanation.Kind, token);
                break;
            case "plan":
                result = await services.GetRequiredService<ICurriculumPlanner>().PlanAsync(step.ReadInput<CurriculumRequest>(), token);
                break;
            case "teach":
                result = await services.GetRequiredService<ITeachingPlanner>().ProposeAsync(step.ReadInput<TeachingInput>().ToRequest(), token);
                break;
            default: throw new InvalidOperationException("Unsupported Learning provider step.");
        }
        return JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
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
