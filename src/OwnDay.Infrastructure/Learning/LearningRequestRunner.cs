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
using OwnDay.Application.Dialogue;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

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
                var handler = CreateHandler(services, session, lease.RequestId);
                return await services.GetRequiredService<ILearningRequestStore>().ApplyAsync(lease, async (request, cancellationToken) =>
                {
                    if (request.AttemptCount > maxAttempts) return new(null, "attempts_exhausted");
                    if (request.PayloadKind == "learning_text" && request.PayloadVersion == 1 ||
                        request.PayloadKind == "learning_command" && request.PayloadVersion == 3)
                        return await ExecuteDialogueAsync(services, db, request, session, handler, options.Value, cancellationToken);
                    if (request.PayloadKind != "learning_command" || request.PayloadVersion is not (1 or 2)) return new(null, "unsupported_payload");
                    LearningCommandInput? input;
                    try { input = JsonSerializer.Deserialize<LearningCommandInput>(request.NormalizedInput, JsonSerializerOptions.Web); }
                    catch (JsonException) { return new(null, "unsupported_payload"); }
                    if (input is null || input.Name is null || input.Arguments is null || !handler.Handles(input.Name))
                        return new(null, "unsupported_payload");
                    if (request.PayloadVersion == 2 && input.Name is "answer" or "learn_ask" or "learn_explain")
                    {
                        var lesson = await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, cancellationToken);
                        if (input.LessonId is null || lesson?.Id != input.LessonId)
                            return new(null, "stale_target")
                            {
                                Replies = ["The addressed question is no longer current or was not available when the command arrived. Check the current lesson and repeat the command; no answer was assessed."]
                            };
                    }
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
                    async (request, cancellationToken) =>
                    {
                        await SynchronizeTerminalDialogueAsync(scope.ServiceProvider, request, cancellationToken);
                        return new LearningRequestExecution(null, "stale_target")
                        {
                            Replies = ["Learning context changed while the request was being processed. No result was applied. Check the current lesson or repeat the original command."]
                        };
                    }, token);
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
                    async (request, cancellationToken) =>
                    {
                        await SynchronizeTerminalDialogueAsync(scope.ServiceProvider, request, cancellationToken);
                        return new LearningRequestExecution(null, "provider_outcome_unknown")
                        {
                            Replies = ["The provider outcome is unknown. No result was applied or answer assessed. Check the current lesson before explicitly retrying the original command."]
                        };
                    }, token);
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

    private static TelegramLearningCommandHandler CreateHandler(IServiceProvider services, LearningProviderSession session, Guid operationId)
    {
        var knowledge = services.GetRequiredService<KnowledgeService>();
        var lessons = services.GetRequiredService<ILessonStore>();
        var programs = services.GetRequiredService<ICurriculumStore>();
        var learning = new LearningService(lessons, session, knowledge, operationId, programs);
        var curriculum = CreateCurriculum(services, session);
        var study = new StudyService(services.GetRequiredService<IStudyScheduleStore>(), lessons, programs,
            services.GetRequiredService<IKnowledgeStore>(), learning, curriculum, services.GetRequiredService<TimeProvider>());
        return new(learning, new(curriculum), new(knowledge, learning), new(study));
    }

    private static CurriculumService CreateCurriculum(IServiceProvider services, LearningProviderSession session) =>
        new(services.GetRequiredService<ICurriculumStore>(), session, services.GetRequiredService<ILessonStore>(), session,
            services.GetRequiredService<StructuredItemService>(), services.GetRequiredService<KnowledgeService>(), session,
            services.GetRequiredService<IActionStore>());

    private static LearningPracticeService CreatePractice(IServiceProvider services, LearningProviderSession session) =>
        new(services.GetRequiredService<ICurriculumStore>(), services.GetRequiredService<ILessonStore>(),
            new(services.GetRequiredService<ILessonStore>(), session, services.GetRequiredService<KnowledgeService>()),
            CreateCurriculum(services, session));

    private static async Task<LearningRequestExecution> ExecuteDialogueAsync(IServiceProvider services, OwnDayDbContext db,
        LearningRequest request, LearningProviderSession session, TelegramLearningCommandHandler handler, LearningRequestProcessingOptions processing, CancellationToken token)
    {
        LearningCommandInput? command = null;
        LearningTextInput? text = null;
        try
        {
            if (request.PayloadKind == "learning_command") command = JsonSerializer.Deserialize<LearningCommandInput>(request.NormalizedInput, JsonSerializerOptions.Web);
            else text = JsonSerializer.Deserialize<LearningTextInput>(request.NormalizedInput, JsonSerializerOptions.Web);
        }
        catch (JsonException) { return new(null, "unsupported_payload"); }
        if (command is null && (text?.Text is null || text.Text.Length is < 1 or > 3000) ||
            command is not null && (command.Arguments is null || command.Arguments.Length > 4000 ||
                !handler.Handles(command.Name) && command.Name != "cancel")) return new(null, "unsupported_payload");
        var lesson = await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, token);
        var store = services.GetRequiredService<IDialogueStore>();
        var program = await store.ReadProgramAsync(request.UserId, token);
        var now = new DateTimeOffset(await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token));
        var pedagogicalStamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            TargetStamp(db, includeDialogue: false) + JsonSerializer.Serialize(program, JsonSerializerOptions.Web))));
        var turn = new DialogueTurn(request.Id, request.UserId, command is null ? null : CommandKind(command.Name),
            command?.Arguments ?? text!.Text, command?.LessonId ?? text?.LessonId, command?.PromptId ?? text?.PromptId,
            new DateTimeOffset(request.ReceivedAt), now, lesson is null ? null : db.Entry(lesson).Property<uint>("TargetVersion").OriginalValue,
            request.SourceMessageId ?? request.SourceUpdateId);
        var decision = await new LearningDialogueService(store, session, processing.ConsultationEnabled || text?.ConsultationAllowed == true,
            processing.PracticeEnabled || text?.PracticeAllowed == true,
            processing.ProgramSwitchEnabled || text?.ProgramSwitchAllowed == true || command?.ProgramSwitchAllowed == true,
            services.GetRequiredService<LearningSessionService>())
            .DecideAsync(turn, lesson, program, pedagogicalStamp, token);
        var introduce = db.Entry(decision.Context).State == EntityState.Added;
        var audit = decision.Audit is null ? null : JsonSerializer.Serialize(decision.Audit, JsonSerializerOptions.Web);
        if (decision.Creation is { } creation)
        {
            var result = await CreateCurriculum(services, session).CreateAsync(request.UserId, creation.Goal, creation.Experience, null, token,
                processing.EnglishDiagnosticEnabled || text?.EnglishDiagnosticAllowed == true, goalCriterion: creation.GoalCriterion,
                subjectDiagnostic: true);
            session.EnsureComplete();
            var completed = FirstGoalConsultation.CompleteCreation(decision.Context, result, turn);
            return new(null, completed.Audit!.Code)
                { Replies = Introduce(introduce, TelegramDialoguePresenter.Render(completed.Reply!)) };
        }
        if (decision.Execute is not { } operation)
        {
            var reply = decision.Reply ?? new(DialogueReplyKind.Unavailable);
            var dialogueReplies = TelegramDialoguePresenter.Render(reply);
            if (decision.SuggestReviewsOnReturn && reply.Kind == DialogueReplyKind.Recap)
            {
                var suggestions = await CreateCurriculum(services, session).SuggestReviewsAsync(request.UserId, token);
                if (suggestions.FirstOrDefault() is { } suggestion)
                    dialogueReplies = [.. dialogueReplies, TelegramKnowledgePresenter.ReviewAfterResume(suggestion)];
            }
            session.EnsureComplete();
            return new(null, decision.Audit?.Code ?? "dialogue_reply")
                { Replies = Introduce(introduce, dialogueReplies), DialogueOutcomeJson = audit };
        }
        if (operation == DialogueOperationKind.ReviewSuggestions || decision.SuggestReviewsOnReturn)
        {
            var suggestions = await CreateCurriculum(services, session).SuggestReviewsAsync(request.UserId, token);
            if (suggestions.Count > 0 || operation == DialogueOperationKind.ReviewSuggestions)
            {
                session.EnsureComplete();
                return new(null, "review_suggestions") { Replies = Introduce(introduce, TelegramKnowledgePresenter.Suggestions(suggestions)) };
            }
        }
        if (operation is DialogueOperationKind.ViewRoute or DialogueOperationKind.EditRoute)
        {
            var curriculum = CreateCurriculum(services, session);
            CurriculumResult result;
            if (operation == DialogueOperationKind.ViewRoute)
                result = await curriculum.ViewRouteAsync(request.UserId, decision.Arguments, token);
            else if (decision.Audit?.Confirmation?.Target is { ProgramId: { } programId, RouteVersion: { } version })
                result = await curriculum.EditRouteAsync(request.UserId, programId, version, decision.Arguments, token);
            else
                result = new CurriculumResult.RouteEditRejected();
            session.EnsureComplete();
            if (operation == DialogueOperationKind.EditRoute) decision.Context.Synchronize(lesson, now, turn.SourceOrder);
            return new(null, result is CurriculumResult.RouteEditRejected ? "route_rejected" : "route_reply")
                { Replies = Introduce(introduce, TelegramCurriculumPresenter.Render(result)), DialogueOutcomeJson = audit };
        }
        if (operation is DialogueOperationKind.ProposeGoalUpdate or DialogueOperationKind.ApplyGoalUpdate)
        {
            var curriculum = CreateCurriculum(services, session);
            CurriculumResult result = new CurriculumResult.GoalEditRejected();
            if (operation == DialogueOperationKind.ProposeGoalUpdate && program is not null)
                result = await curriculum.ProposeGoalUpdateAsync(request.UserId, program.Id, program.RouteVersion, decision.Arguments, token);
            if (operation == DialogueOperationKind.ApplyGoalUpdate && decision.Audit?.Confirmation?.Target is { ProgramId: { } programId, RouteVersion: { } version })
                result = await curriculum.ApplyGoalUpdateAsync(request.UserId, programId, version, decision.Arguments, token);
            session.EnsureComplete();
            var goalReplies = TelegramCurriculumPresenter.Render(result);
            if (result is CurriculumResult.GoalUpdateOffered proposal)
            {
                var confirmation = LearningGoalChange.OpenUpdate(decision.Context, turn, program!, lesson, pedagogicalStamp, proposal);
                goalReplies = [.. goalReplies, .. TelegramDialoguePresenter.Render(new(DialogueReplyKind.ConfirmGoalUpdate,
                    [confirmation.Label!, confirmation.Code]))];
                audit = JsonSerializer.Serialize(new DialogueAudit("confirmation_published", Confirmation: confirmation,
                    OriginalRequestId: turn.RequestId), JsonSerializerOptions.Web);
            }
            else if (operation == DialogueOperationKind.ApplyGoalUpdate)
                decision.Context.Synchronize(lesson, now, turn.SourceOrder);
            return new(null, result is CurriculumResult.GoalEditRejected ? "goal_update_rejected" : "goal_update_reply")
                { Replies = Introduce(introduce, goalReplies), DialogueOutcomeJson = audit };
        }
        if (operation is DialogueOperationKind.ExplainAssessment or DialogueOperationKind.AppealAssessment)
        {
            var assessments = new AssessmentReviewService(services.GetRequiredService<IAssessmentReviewStore>(),
                services.GetRequiredService<IKnowledgeStore>(), session, services.GetRequiredService<TimeProvider>());
            var result = operation == DialogueOperationKind.AppealAssessment
                ? await assessments.ReviewAsync(request.UserId, request.Id, decision.Arguments, token)
                : await assessments.ExplainAsync(request.UserId, program?.Id, decision.Arguments, token);
            session.EnsureComplete();
            return new(null, "assessment_review") { Replies = TelegramAssessmentReviewPresenter.Render(result) };
        }
        if (operation == DialogueOperationKind.Diagnose && program?.HasEnglishDiagnostic == true && lesson is null)
        {
            var result = await new EnglishDiagnosticDialogue(services.GetRequiredService<ICurriculumStore>(),
                CreateCurriculum(services, session), session).HandleAsync(decision.Context, turn, decision.Arguments, token);
            session.EnsureComplete();
            if (result.Result is CurriculumResult.Lesson)
            {
                var current = db.Set<Lesson>().Local.SingleOrDefault(value => value.UserId == request.UserId &&
                    value.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused);
                decision.Context.Synchronize(current, now, turn.SourceOrder);
            }
            return new(null, result.Outcome) { Replies = Introduce(introduce, TelegramCurriculumPresenter.Render(result.Result)) };
        }
        if (operation == DialogueOperationKind.CompleteDiagnostic)
        {
            var result = await CreateCurriculum(services, session).CompleteDiagnosticAndStartAsync(request.UserId, decision.Arguments, token);
            if (result is CurriculumResult.SubjectDiagnosticQuestion question)
            {
                if (!decision.Context.MatchesDiagnosticQuestion(question.Value.Id, question.Value.DiagnosticQuestion!))
                    decision.Context.BindDiagnosticQuestion(question.Value.Id, question.Value.DiagnosticQuestion!, now, turn.SourceOrder);
            }
            else if (result is CurriculumResult.Lesson or CurriculumResult.RouteCompleted)
            {
                var current = db.Set<Lesson>().Local.SingleOrDefault(value => value.UserId == request.UserId &&
                    value.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused);
                decision.Context.Synchronize(current, now, turn.SourceOrder);
            }
            session.EnsureComplete();
            return new(null, "diagnostic_reply") { Replies = Introduce(introduce, TelegramCurriculumPresenter.Render(result)) };
        }
        if (operation is DialogueOperationKind.SwitchProgram or DialogueOperationKind.SwitchGoal or DialogueOperationKind.SwitchGoalWithCriterion or
            DialogueOperationKind.SwitchCreateProgram or DialogueOperationKind.SwitchCreateProjectProgram)
        {
            IReadOnlyList<string> switched;
            if (operation is DialogueOperationKind.SwitchGoal or DialogueOperationKind.SwitchGoalWithCriterion)
            {
                var goal = operation == DialogueOperationKind.SwitchGoalWithCriterion
                    ? decision.Arguments.Split('|', 2, StringSplitOptions.TrimEntries) : [decision.Arguments];
                var result = await CreateCurriculum(services, session).CreateAsync(request.UserId, goal[0],
                    "Опыт по новой цели не уточнён", null, token,
                    englishDiagnostic: processing.EnglishDiagnosticEnabled || text?.EnglishDiagnosticAllowed == true || program?.HasEnglishDiagnostic == true,
                    suspendCurrent: true, goalCriterion: goal.Length == 2 ? goal[1] : null);
                var reply = LearningGoalChange.Complete(decision.Context, result, turn);
                switched = result is CurriculumResult.Program ? TelegramDialoguePresenter.Render(reply) : TelegramCurriculumPresenter.Render(result);
            }
            else
            {
                var switchName = operation == DialogueOperationKind.SwitchProgram ? "learn_select" :
                    operation == DialogueOperationKind.SwitchCreateProgram ? "learn_request" : "learn_project";
                switched = await new TelegramCurriculumCommandHandler(CreateCurriculum(services, session))
                    .HandleAsync(new(switchName, decision.Arguments, request.UserId), token, suspendCurrent: true);
            }
            session.EnsureComplete();
            var restored = await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, token);
            if (decision.Context.DiagnosticTarget is null) decision.Context.Synchronize(restored, now, turn.SourceOrder);
            if (restored is not null) switched = [.. switched, .. TelegramLearningPresenter.Render(LearningService.Session(restored))];
            return new(null, decision.Audit?.Code ?? "program_switched") { Replies = switched, DialogueOutcomeJson = audit };
        }
        if (operation is DialogueOperationKind.BeginPractice or DialogueOperationKind.NextPractice or DialogueOperationKind.PausePractice or
            DialogueOperationKind.ResumePractice or DialogueOperationKind.StopPractice or DialogueOperationKind.PracticeInfo)
        {
            var result = await CreatePractice(services, session).ExecuteAsync(request.UserId, operation, decision.Arguments, token);
            session.EnsureComplete();
            var current = db.Set<Lesson>().Local.SingleOrDefault(value => value.UserId == request.UserId &&
                value.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused);
            decision.Context.Synchronize(current, now, turn.SourceOrder);
            return new(null, "practice_reply") { Replies = TelegramPracticePresenter.Render(result) };
        }
        var name = operation is DialogueOperationKind.None or DialogueOperationKind.StartOtherLesson ? command!.Name : CommandName(operation);
        var replies = await handler.HandleAsync(new(name, decision.Arguments, request.UserId), token, program?.Discipline);
        var practiceResult = await CreatePractice(services, session).SynchronizeAsync(request.UserId, operation, token);
        if (practiceResult is not null) replies = [.. replies, .. TelegramPracticePresenter.Render(practiceResult)];
        session.EnsureComplete();
        var active = db.Set<Lesson>().Local.SingleOrDefault(value => value.UserId == request.UserId &&
            value.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused);
        if (operation is not (DialogueOperationKind.Progress or DialogueOperationKind.PreferenceView or DialogueOperationKind.PreferenceSet or DialogueOperationKind.PreferenceReset))
            decision.Context.Synchronize(active, now, turn.SourceOrder);
        return new(null, decision.Audit?.Code ?? "reply_committed") { Replies = Introduce(introduce, replies), DialogueOutcomeJson = audit };
    }

    private static IReadOnlyList<string> Introduce(bool introduce, IReadOnlyList<string> replies) => introduce
        ? [TelegramDialoguePresenter.Introduction, .. replies] : replies;

    private static DialogueOperationKind CommandKind(string name) => name switch
    {
        "answer" => DialogueOperationKind.Answer, "learn_ask" => DialogueOperationKind.Clarify,
        "learn_explain" => DialogueOperationKind.Explain, "learn_pause" => DialogueOperationKind.Pause,
        "learn_resume" => DialogueOperationKind.Resume, "learn_finish" => DialogueOperationKind.Finish,
        "learn_end_session" => DialogueOperationKind.EndSession,
        "learn_solution" => DialogueOperationKind.RevealSolution,
        "learn_assessment" => DialogueOperationKind.ExplainAssessment, "learn_appeal" => DialogueOperationKind.AppealAssessment,
        "learn_route" => DialogueOperationKind.ViewRoute, "learn_route_edit" => DialogueOperationKind.EditRoute,
        "learn_goal_update" => DialogueOperationKind.ProposeGoalUpdate,
        "learn_recommend_reviews" => DialogueOperationKind.ReviewSuggestions,
        "learn_preferences" => DialogueOperationKind.PreferenceView,
        "learn_preference" => DialogueOperationKind.PreferenceSet,
        "learn_preference_reset" => DialogueOperationKind.PreferenceReset,
        "learn_next" => DialogueOperationKind.Next, "learn_progress" => DialogueOperationKind.Progress,
        "learn_request" or "learn_program" => DialogueOperationKind.CreateProgram,
        "learn_project" => DialogueOperationKind.CreateProjectProgram, "learn_select" => DialogueOperationKind.SelectProgram,
        "learn_confirm" => DialogueOperationKind.Confirm, "cancel" => DialogueOperationKind.Cancel,
        "learn_diagnose" => DialogueOperationKind.Diagnose,
        "learn" or "learn_practice" or "learn_review" or "learn_focus" or "learn_adapt" or "learn_project_practice" or "learn_transfer" or "learn_invite_start" => DialogueOperationKind.StartOtherLesson,
        _ => DialogueOperationKind.None
    };
    private static string CommandName(DialogueOperationKind operation) => operation switch
    {
        DialogueOperationKind.Answer => "answer", DialogueOperationKind.Clarify => "learn_ask",
        DialogueOperationKind.Explain => "learn_explain", DialogueOperationKind.RevealSolution => "learn_solution", DialogueOperationKind.Pause => "learn_pause",
        DialogueOperationKind.Resume => "learn_resume", DialogueOperationKind.Finish => "learn_finish",
        DialogueOperationKind.Next => "learn_next", DialogueOperationKind.Progress => "learn_progress",
        DialogueOperationKind.CreateProgram => "learn_request", DialogueOperationKind.CreateProjectProgram => "learn_project",
        DialogueOperationKind.SelectProgram => "learn_select", DialogueOperationKind.Diagnose => "learn_diagnose",
        DialogueOperationKind.PreferenceView => "learn_preferences", DialogueOperationKind.PreferenceSet => "learn_preference",
        DialogueOperationKind.PreferenceReset => "learn_preference_reset",
        _ => throw new InvalidOperationException("Unsupported dialogue action.")
    };

    private static async Task SynchronizeTerminalDialogueAsync(IServiceProvider services, LearningRequest request, CancellationToken token)
    {
        if (request.PayloadKind != "learning_text" && request.PayloadVersion != 3) return;
        var store = services.GetRequiredService<IDialogueStore>();
        var db = services.GetRequiredService<OwnDayDbContext>();
        var now = new DateTimeOffset(await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token));
        var lesson = await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, token);
        var context = await store.FindAsync(request.UserId, token);
        if (context is null) store.Add(DialogueContext.Create(request.UserId, lesson, now));
        else if (context.SchemaVersion == DialogueContext.CurrentSchemaVersion && context.Consultation is not { Version: not 1 } &&
            (context.InterruptedAt is null || request.ReceivedAt >= context.InterruptedAt.Value.UtcDateTime))
            context.Synchronize(lesson, now, request.SourceMessageId ?? request.SourceUpdateId);
    }

    private static string TargetStamp(OwnDayDbContext db, bool includeDialogue = true)
    {
        // Addressed original row values, including keys, route versions and preferences.
        // Repeating the use case also rechecks absence, owner and prerequisite queries.
        // Newly proposed state is never part of a stamp and never survives prepare.
        var rows = db.ChangeTracker.Entries().Where(entry => entry.State != EntityState.Added &&
                (includeDialogue || entry.Entity is not DialogueContext) &&
                entry.Entity.GetType().Namespace?.StartsWith("OwnDay.Domain", StringComparison.Ordinal) == true &&
                entry.Entity is not LearningSession)
            .Select(entry => entry.Metadata.Name + ":" + JsonSerializer.Serialize(
                entry.OriginalValues.Properties
                    // A legacy program has the same checkpoint stamp on either side of the additive migration.
                    .Where(property => property.Name is not (nameof(LearningProgram.GoalCriterion) or nameof(LearningProgram.SubjectDiagnostic) or
                        nameof(Lesson.RubricEvaluation) or nameof(LearningEvidence.RubricEvidence) or
                        nameof(LearningEvidence.ReviewHistory) or nameof(LearningEvidence.ReassessmentCredit) or nameof(CurriculumRoute.Path) or
                        nameof(LearningPreference.ResetThrough) or nameof(LessonInteraction.ExplanationPreferences)) || entry.OriginalValues[property] is not null)
                    .Where(property => property.Name != nameof(LearningPreference.Scope) || entry.OriginalValues[property] is not "")
                    .Where(property => entry.Entity is not StudySchedule || property.Name is not (nameof(StudySchedule.LessonId) or
                        nameof(StudySchedule.QuestionFingerprint) or nameof(StudySchedule.ReminderTypes)) || entry.OriginalValues[property] is not null)
                    .Where(property => entry.Entity is not LessonInteraction || property.Name != nameof(LessonInteraction.Provenance) || entry.OriginalValues[property] is not null)
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
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
            Key = entry.Metadata.FindPrimaryKey()!.Properties
                .Where(property => property.Name != nameof(LearningPreference.Scope) || entry.OriginalValues[property] is not "")
                .ToDictionary(property => property.Name, property => entry.OriginalValues[property]),
            Version = entry.OriginalValues["TargetVersion"]
        }).OrderBy(target => target.Type, StringComparer.Ordinal).ThenBy(target => JsonSerializer.Serialize(target.Key), StringComparer.Ordinal),
        JsonSerializerOptions.Web);
}
