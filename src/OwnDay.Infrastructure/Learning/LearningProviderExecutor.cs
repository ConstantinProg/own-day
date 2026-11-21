using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Application.Dialogue;
using OwnDay.Infrastructure.Persistence;

namespace OwnDay.Infrastructure.Learning;

// Physical calls retain their slots until the actual adapter task completes. An
// unresponsive call closes admission on this Host, including after logical timeout.
public sealed class LearningProviderCallGate(IOptions<LearningRequestProcessingOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Value.Parallelism);
    private readonly ConcurrentDictionary<Task, byte> _unresponsive = new();
    private int _stopped;

    public bool TryEnter() => Volatile.Read(ref _stopped) == 0 && _unresponsive.IsEmpty && _slots.Wait(0);
    public void Release()
    {
        _slots.Release();
        if (Volatile.Read(ref _stopped) != 0 && _slots.CurrentCount == options.Value.Parallelism) _slots.Dispose();
    }

    public void MarkUnresponsive(Task task)
    {
        if (task.IsCompleted) return;
        _unresponsive.TryAdd(task, 0);
        task.ConfigureAwait(false).GetAwaiter().OnCompleted(() =>
        {
            _ = task.Exception; // Observe late faults; a late result never applies Domain state.
            _unresponsive.TryRemove(task, out _);
        });
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stopped, 1);
        if (_slots.CurrentCount == options.Value.Parallelism) _slots.Dispose();
    }
}

public sealed record LearningProviderExecution(string? Result = null, LessonProviderFailure? Failure = null,
    bool Unknown = false, bool OwnershipLost = false);

public sealed class LearningProviderExecutor(IServiceScopeFactory scopes, IOptions<LearningRequestProcessingOptions> options,
    LearningProviderCallGate gate, ILogger<LearningProviderExecutor> logger)
{
    public async Task<LearningProviderExecution> ExecuteAsync(LearningRequestLease lease, LearningProviderStep step, CancellationToken token)
    {
        for (var retry = 0; retry < options.Value.MaxProviderAttempts; retry++)
        {
            token.ThrowIfCancellationRequested();
            if (!gate.TryEnter()) return new(Failure: LessonProviderFailure.Unavailable);
            EfLearningRequestStore.AttemptAdmission admission;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                admission = await scope.ServiceProvider.GetRequiredService<EfLearningRequestStore>().BeginAttemptAsync(
                    lease, step.Ordinal, options.Value.MaxProviderAttempts, TimeSpan.FromSeconds(options.Value.LeaseSeconds), token);
            }
            catch { gate.Release(); throw; }
            if (admission.Id is not { } attemptId)
            {
                gate.Release();
                return new(Failure: LessonProviderFailure.Unavailable, Unknown: admission.Unknown, OwnershipLost: admission.OwnershipLost);
            }
            // The underlying task owns both its scope and slot, even if the caller
            // stops waiting. No tracked preparation objects enter that scope.
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.ProviderTimeoutSeconds));
            var invocationToken = deadline.Token;
            var physical = InvokePhysicalAsync(attemptId, step, deadline);
            LearningProviderExecution result;
            try { result = await physical.WaitAsync(invocationToken); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                gate.MarkUnresponsive(physical);
                return new(Unknown: true);
            }
            catch (OperationCanceledException)
            {
                gate.MarkUnresponsive(physical);
                throw;
            }
            if (result.Failure is not (LessonProviderFailure.HttpError or LessonProviderFailure.Timeout) ||
                retry + 1 == options.Value.MaxProviderAttempts) return result;
            await Task.Delay(200, token);
        }
        return new(Failure: LessonProviderFailure.Unavailable);
    }

    private async Task<LearningProviderExecution> InvokePhysicalAsync(Guid attemptId, LearningProviderStep step, CancellationTokenSource deadline)
    {
        var outcome = "unexpected_failure";
        LearningProviderUsage? usage = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            usage = services.GetRequiredService<LearningProviderUsage>();
            var providerOptions = services.GetRequiredService<IOptions<OpenAIInboxAnalysisOptions>>().Value;
            usage.LedgerAttemptId = await services.GetRequiredService<IOpenAIRequestLedger>().AdmitLearningAsync(attemptId,
                new InboxAnalyzerDescriptor(providerOptions.Model, step.Kind + "-v" + step.Version,
                    (step.Kind == "intent" ? "learning-intent-v" : "learning-v") + step.Version,
                    ReasoningEffort: step.Kind == "intent" && providerOptions.Model == "gpt-6-luna" ? "none" : null,
                    MaxOutputTokens: step.Kind == "intent" ? Math.Min(512, providerOptions.MaxOutputTokens) : providerOptions.MaxOutputTokens,
                    TimeoutSeconds: providerOptions.TimeoutSeconds), deadline.Token);
            if (usage.LedgerAttemptId is null) throw new LessonProviderException(LessonProviderFailure.BudgetLimit);
            var result = await InvokeAsync(scope.ServiceProvider, step, deadline.Token);
            outcome = "succeeded";
            return new(Result: result);
        }
        catch (LessonProviderException failure)
        {
            outcome = failure.Reason is LessonProviderFailure.HttpError or LessonProviderFailure.Timeout
                ? "transient_failure" : "permanent_failure";
            return new(Failure: failure.Reason);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            // Bounded invocation/financial reconciliation outlives the logical
            // deadline/Host token, without changing checkpoints or Domain state.
            using var reconciliation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (usage?.LedgerAttemptId is { } ledgerId)
                    await scope.ServiceProvider.GetRequiredService<IOpenAIRequestLedger>().FinishAsync(ledgerId,
                        outcome == "succeeded" ? "response" : outcome, usage.Response, reconciliation.Token);
                await scope.ServiceProvider.GetRequiredService<EfLearningRequestStore>().FinishAttemptAsync(attemptId, outcome, reconciliation.Token);
            }
            catch (Exception exception)
            {
                logger.LogWarning("Learning attempt {AttemptId} remains unknown ({ErrorType}).", attemptId, exception.GetType().Name);
            }
            deadline.Dispose();
            gate.Release();
        }
    }

    private static async Task<string> InvokeAsync(IServiceProvider services, LearningProviderStep step, CancellationToken token)
    {
        object result;
        switch (step.Kind)
        {
            case "intent":
                result = await services.GetRequiredService<ILearningIntentResolver>().ResolveAsync(step.ReadInput<LearningIntentInput>(), token);
                break;
            case "generate":
                result = await services.GetRequiredService<ILessonTeacher>().GenerateAsync(step.ReadInput<LessonRequest>(), token);
                break;
            case "assess":
                var assessment = step.ReadInput<LessonAssessmentInput>();
                result = await services.GetRequiredService<ILessonTeacher>().AssessAsync(assessment.Lesson, assessment.Answer, token);
                break;
            case "interpret_choice":
                result = await services.GetRequiredService<ILessonTeacher>().InterpretChoiceAsync(step.ReadInput<ChoiceInterpretationInput>(), token);
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
}
