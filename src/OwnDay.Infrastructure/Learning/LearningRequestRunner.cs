using System.Security.Cryptography;
using System.Diagnostics;
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
using OwnDay.Infrastructure.Telegram.Actions;
using OwnDay.Infrastructure.Telegram.Commands;
using OwnDay.Application.Dialogue;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Diagnostics;

namespace OwnDay.Infrastructure.Learning;

public sealed partial class LearningRequestRunner(IServiceScopeFactory scopes, IOptions<LearningRequestProcessingOptions> options,
    LearningProviderExecutor provider, TelegramActionKeyboard keyboard)
    : ILearningRequestRunner
{
    public async Task<bool> RunAsync(LearningRequestLease lease, int maxAttempts, CancellationToken token)
    {
        // Up to two required steps and one optional continuation, followed by apply. Each iteration disposes all
        // tracked state before any external I/O; a provider is never available in apply.
        for (var phase = 0; phase <= LearningProviderSession.MaxSteps; phase++)
        {
            LearningProviderStep pending;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var db = services.GetRequiredService<OwnDayDbContext>();
                var steps = await db.Set<LearningProviderStep>().AsNoTracking()
                    .Where(step => step.RequestId == lease.RequestId).OrderBy(step => step.Ordinal).ToListAsync(token);
                return await services.GetRequiredService<EfLearningRequestStore>().ApplyAsync(lease, async (request, cancellationToken) =>
                {
                    var language = ReadLanguage(request);
                    bool? russian = language is null ? null : language.Locale == "ru";
                    var session = new LearningProviderSession(steps, () => TargetStamp(db), () => TargetIdentities(db),
                        language is { IsExplicit: true } ? language.Locale : null);
                    var handler = CreateHandler(services, session, lease.RequestId, russian);
                    var execution = await ExecuteRequestAsync(services, db, request, session, handler, maxAttempts, russian, cancellationToken);
                    return await PrepareCompletionAsync(services, db, request, execution, russian, cancellationToken);
                }, token);
            }
            catch (LearningProviderRequiredException required) { pending = required.Step; }
            catch (StaleLearningPreparationException)
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<EfLearningRequestStore>().ApplyAsync(lease,
                    async (request, cancellationToken) =>
                    {
                        await SynchronizeTerminalDialogueAsync(scope.ServiceProvider, request, cancellationToken);
                        return new LearningRequestCompletion("stale_target")
                        {
                            Replies = [ReadLanguage(request)?.Locale == "ru"
                                ? "Учебный контекст изменился во время обработки. Результат не применён. Проверьте текущий урок или повторите исходную команду."
                                : "Learning context changed while the request was being processed. No result was applied. Check the current lesson or repeat the original command."]
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
            if (execution.Unknown && pending.Kind != LearningProviderSession.ContinuationStep)
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<EfLearningRequestStore>().ApplyAsync(lease,
                    async (request, cancellationToken) =>
                    {
                        await SynchronizeTerminalDialogueAsync(scope.ServiceProvider, request, cancellationToken);
                        return new LearningRequestCompletion("provider_outcome_unknown")
                        {
                            Replies = [UnknownProviderReply(pending, ReadLanguage(request)?.Locale == "ru")]
                        };
                    }, token);
            }
            pending.Result = execution.Result;
            pending.Failure = execution.Unknown ? LessonProviderFailure.Unavailable : execution.Failure;
            token.ThrowIfCancellationRequested();
            await using (var scope = scopes.CreateAsyncScope())
            {
                if (!await scope.ServiceProvider.GetRequiredService<EfLearningRequestStore>().SaveStepAsync(
                    lease, pending, TimeSpan.FromSeconds(options.Value.LeaseSeconds), token)) return false;
            }
        }
        throw new InvalidOperationException("Learning command exceeded its phase limit.");
    }

    private static string UnknownProviderReply(LearningProviderStep step, bool russian)
    {
        if (step.Kind == "generate")
        {
            if (step.ReadInput<LessonRequest>().Program is not null) return russian
                ? "Программа сохранена. Новый урок не создан. Откройте программу и повторите запуск урока."
                : "Your program is saved. No new lesson was created. Open the program to start the lesson again.";
            return russian ? "Новый урок не создан. Можно повторить команду запуска урока."
                : "No new lesson was created. You can repeat the command to start the lesson.";
        }
        if (step.Kind == "assess") return russian
            ? "Ответ не оценён. Проверьте текущий урок перед повторной отправкой ответа."
            : "Your answer was not assessed. Check the current lesson before submitting your answer again.";
        return russian ? "Результат запроса неизвестен. Проверьте текущий контекст перед повтором команды."
            : "The provider outcome is unknown. Check your current learning context before repeating the command.";
    }

    private sealed record LanguageEnvelope(LearningReplyLanguage? Language);

    private static LearningReplyLanguage? ReadLanguage(LearningRequest request)
    {
        try
        {
            var language = JsonSerializer.Deserialize<LanguageEnvelope>(request.NormalizedInput, JsonSerializerOptions.Web)?.Language;
            return language?.Locale is "en" or "ru" ? language : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<LearningRequestExecution> ExecuteRequestAsync(IServiceProvider services, OwnDayDbContext db,
        LearningRequest request, LearningProviderSession session, TelegramLearningCommandHandler handler, int maxAttempts,
        bool? russian, CancellationToken token)
    {
        if (request.AttemptCount > maxAttempts) return new("attempts_exhausted");
        if (request.PayloadKind == "learning_action" && request.PayloadVersion == 1)
            return await ExecuteActionAsync(services, db, request, session, handler, options.Value, russian, token);
        if (request.PayloadKind == "learning_choice" && request.PayloadVersion == 1)
            return await ExecuteChoiceAsync(services, db, request, session, russian, token);
        if (request.PayloadKind == "learning_text" && request.PayloadVersion == 1 ||
            request.PayloadKind == "learning_command" && request.PayloadVersion == 3)
            return await ExecuteDialogueAsync(services, db, request, session, handler, options.Value, russian, token);
        if (request.PayloadKind != "learning_command" || request.PayloadVersion is not (1 or 2)) return new("unsupported_payload");
        LearningCommandInput? input;
        try { input = JsonSerializer.Deserialize<LearningCommandInput>(request.NormalizedInput, JsonSerializerOptions.Web); }
        catch (JsonException) { return new("unsupported_payload"); }
        if (input is null || input.Name is null || input.Arguments is null || !handler.Handles(input.Name))
            return new("unsupported_payload");
        if (request.PayloadVersion == 2 && input.Name is "answer" or "learn_ask" or "learn_explain")
        {
            var lesson = await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, token);
            if (input.LessonId is null || lesson?.Id != input.LessonId)
                return new("stale_target",
                    ["The addressed question is no longer current or was not available when the command arrived. Check the current lesson and repeat the command; no answer was assessed."]);
        }
        var replies = await handler.HandleAsync(new(input.Name, input.Arguments, request.UserId), token, inRussian: russian);
        session.EnsureComplete();
        return new("reply_committed", replies);
    }

    private async Task<LearningRequestCompletion> PrepareCompletionAsync(IServiceProvider services, OwnDayDbContext db,
        LearningRequest request, LearningRequestExecution execution, bool? russian, CancellationToken token)
    {
        var replies = execution.Replies;
        if (replies is null && russian == true)
            replies = ["Не удалось обработать учебный запрос. Повторите исходную команду. Ответ не был оценён."];
        if (request.ChatId == request.UserId.Value && replies?.Any(reply => reply.Actions is { Count: > 0 }) == true)
        {
            var dialogue = await services.GetRequiredService<IDialogueStore>().FindAsync(request.UserId, token);
            var lesson = await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, token);
            var now = await NowAsync(db, token);
            replies = replies.Select(reply => keyboard.Render(russian.HasValue ? reply with { InRussian = russian } : reply,
                request.UserId.Value, request.ChatId, now, dialogue, lesson)).ToArray();
        }
        return new(execution.Outcome, replies?.Select(reply => new TelegramOutboxIntent(reply.Text, reply.ReplyMarkupJson, reply.EditMessageId)).ToArray())
            { DialogueOutcomeJson = execution.DialogueOutcomeJson };
    }

    private static TelegramLearningCommandHandler CreateHandler(IServiceProvider services, LearningProviderSession session, Guid operationId, bool? russian)
    {
        var knowledge = services.GetRequiredService<KnowledgeService>();
        var lessons = services.GetRequiredService<ILessonStore>();
        var programs = services.GetRequiredService<ICurriculumStore>();
        var learning = new LearningService(lessons, session, knowledge, operationId, programs, russian);
        var curriculum = CreateCurriculum(services, session);
        var study = new StudyService(services.GetRequiredService<IStudyScheduleStore>(), lessons, programs,
            services.GetRequiredService<IKnowledgeStore>(), learning, curriculum, services.GetRequiredService<TimeProvider>());
        return new(learning, new(curriculum), new(knowledge, learning), new(study));
    }

    private static CurriculumService CreateCurriculum(IServiceProvider services, LearningProviderSession session) =>
        new(services.GetRequiredService<ICurriculumStore>(), session, services.GetRequiredService<ILessonStore>(), session,
            services.GetRequiredService<StructuredItemService>(), services.GetRequiredService<KnowledgeService>(), session,
            services.GetRequiredService<IActionStore>(), () => session.RemainingSteps);

    private static LearningPracticeService CreatePractice(IServiceProvider services, LearningProviderSession session) =>
        new(services.GetRequiredService<ICurriculumStore>(), services.GetRequiredService<ILessonStore>(),
            new(services.GetRequiredService<ILessonStore>(), session, services.GetRequiredService<KnowledgeService>(),
                curriculum: services.GetRequiredService<ICurriculumStore>()),
            CreateCurriculum(services, session));

    private static async Task<LearningRequestExecution> ExecuteChoiceAsync(IServiceProvider services, OwnDayDbContext db,
        LearningRequest request, LearningProviderSession session, bool? russian, CancellationToken token)
    {
        ChoiceInput? input;
        try { input = JsonSerializer.Deserialize<ChoiceInput>(request.NormalizedInput, JsonSerializerOptions.Web); }
        catch (JsonException) { return new("unsupported_payload"); }
        if (input is null || request.ChatId != request.UserId.Value || !Enum.IsDefined(input.Action))
            return new("unsupported_payload");

        return await ApplyChoiceAsync(services, request, session, input, null, await NowAsync(db, token), russian, token);
    }

    private static async Task<DateTimeOffset> NowAsync(OwnDayDbContext db, CancellationToken token) =>
        new(await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token));

    private static async Task<LearningRequestExecution> ExecuteActionAsync(IServiceProvider services, OwnDayDbContext db,
        LearningRequest request, LearningProviderSession session, TelegramLearningCommandHandler handler,
        LearningRequestProcessingOptions processing, bool? russian, CancellationToken token)
    {
        LearningActionInput? input;
        try { input = JsonSerializer.Deserialize<LearningActionInput>(request.NormalizedInput, JsonSerializerOptions.Web); }
        catch (JsonException) { return new("unsupported_payload"); }
        if (input?.Action is null || request.ChatId != request.UserId.Value || request.SourceMessageId is not > 0 ||
            !Enum.IsDefined(input.Action.Kind))
            return new("unsupported_payload");
        var now = await NowAsync(db, token);
        if (now >= input.ExpiresAt) return ActionUnavailable(input.InRussian);
        if (input.Action.ToChoiceInput() is { } choice)
            return await ApplyChoiceAsync(services, request, session, choice, input.Action, now, russian, token);

        StudySchedule? invitationSchedule = null;
        if (input.Action.Kind is LearningActionKind.StartInvitation or LearningActionKind.PostponeInvitation or LearningActionKind.SkipInvitation)
        {
            invitationSchedule = await services.GetRequiredService<IStudyScheduleStore>().FindAsync(request.UserId, token);
            if (invitationSchedule?.IsCurrent(input.Action.TargetId, services.GetRequiredService<TimeProvider>().GetUtcNow()) != true)
                return ActionUnavailable(input.InRussian);
            if (input.Action.Kind == LearningActionKind.StartInvitation && invitationSchedule.LessonId is not null &&
                !invitationSchedule.MatchesOpenExercise(await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, token)))
                return ActionUnavailable(input.InRussian);
        }

        var execution = await ExecuteDialogueAsync(services, db, request, session, handler, processing, russian, token, input);
        if (execution.Outcome is "stale_target" or "unsupported_payload") return execution;
        var remove = execution.RemoveSourceKeyboard;
        if (invitationSchedule is not null)
            remove = invitationSchedule.InvitationId != input.Action.TargetId;
        else if (RemovesKeyboard(input.Action.Kind))
        {
            var context = await services.GetRequiredService<IDialogueStore>().FindAsync(request.UserId, token);
            remove = context?.Revision != input.Action.Revision;
        }
        return remove ? execution with { Replies = [.. execution.Replies ?? [], new TelegramLearningReply("", EditMessageId: request.SourceMessageId)] } : execution;
    }

    private static LearningRequestExecution ActionUnavailable(bool russian) => new("stale_target",
        [russian ? "Это действие больше недоступно. Откройте последнее сообщение или напишите «Продолжим»."
            : "This action is out of date. Use the latest message or write “continue”."]);

    private static async Task<LearningRequestExecution> ApplyChoiceAsync(IServiceProvider services,
        LearningRequest request, LearningProviderSession session, ChoiceInput input, LearningAction? action, DateTimeOffset now,
        bool? russian, CancellationToken token)
    {
        var lessons = services.GetRequiredService<ILessonStore>();
        var lesson = await lessons.FindActiveAsync(request.UserId, token);
        var learning = new LearningService(lessons, session, services.GetRequiredService<KnowledgeService>(), request.Id,
            services.GetRequiredService<ICurriculumStore>(), russian);
        var dialogue = new LearningDialogueService(services.GetRequiredService<IDialogueStore>(), session,
            sessions: services.GetRequiredService<LearningSessionService>());
        var turn = new DialogueTurn(request.Id, request.UserId, null, "", input.LessonId, null,
            new DateTimeOffset(request.ReceivedAt), now, SourceOrder: action is null ? request.SourceMessageId ?? request.SourceUpdateId : 0,
            ContextualAction: action, RequestSequence: request.Sequence);
        var isCurrent = lesson?.Id == input.LessonId && lesson.MatchesChoice(input.Version, input.Revision);
        var application = await dialogue.ApplyChoiceAsync(turn, lesson, input, learning, token);
        var reply = action is not null && !isCurrent && application.Reply is LearningResult.ChoiceReply stale
            ? stale with { Prompt = null } : application.Reply;
        var followUp = application.AttemptRecorded
            ? await CreatePractice(services, session).AfterAnswerAsync(lesson, reply, session, token)
            : new LearningAnswerFollowUp.None();
        var replies = TelegramLearningPresenter.RenderAnswerWithFollowUp(reply, followUp,
            russian ?? (lesson is not null && MarketLearningContent.InRussian(lesson.Theory)), russian);
        if (followUp is LearningAnswerFollowUp.Lesson or LearningAnswerFollowUp.Route or LearningAnswerFollowUp.Practice { Value.Lesson: not null })
        {
            var context = await services.GetRequiredService<IDialogueStore>().FindAsync(request.UserId, token);
            context!.Synchronize(await lessons.FindActiveAsync(request.UserId, token), now, turn.SourceOrder);
        }
        if (action is not null && request.SourceMessageId is { } source)
        {
            if (input.Action == ChoiceAction.Toggle && application.Reply is LearningResult.ChoiceReply { Message.Length: 0, Prompt: not null })
                replies = [replies.Single() with { Text = "", EditMessageId = source }];
            else if (application.AttemptRecorded || application.Reply is LearningResult.Session)
                replies = [.. replies, new TelegramLearningReply("", EditMessageId: source)];
        }
        session.EnsureComplete();
        return new("choice_reply", replies);
    }

    private static async Task<LearningRequestExecution> ExecuteDialogueAsync(IServiceProvider services, OwnDayDbContext db,
        LearningRequest request, LearningProviderSession session, TelegramLearningCommandHandler handler,
        LearningRequestProcessingOptions processing, bool? russian, CancellationToken token, LearningActionInput? actionInput = null)
    {
        LearningCommandInput? command = null;
        LearningTextInput? text = null;
        var lesson = await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, token);
        var store = services.GetRequiredService<IDialogueStore>();
        if (actionInput is { Action: var action })
        {
            var boundContext = await store.FindAsync(request.UserId, token);
            if (!LearningActionProvider.MatchesDialogue(action, boundContext) || !LearningActionProvider.CanOffer(action, lesson))
                return ActionUnavailable(actionInput.InRussian);
            (command, text) = await BindDialogueActionAsync(services, request, actionInput, boundContext, lesson, token);
            if (command is null && text is null) return ActionUnavailable(actionInput.InRussian);
        }
        else
        {
            try
            {
                if (request.PayloadKind == "learning_command") command = JsonSerializer.Deserialize<LearningCommandInput>(request.NormalizedInput, JsonSerializerOptions.Web);
                else text = JsonSerializer.Deserialize<LearningTextInput>(request.NormalizedInput, JsonSerializerOptions.Web);
            }
            catch (JsonException) { return new("unsupported_payload"); }
        }
        if (command is null && (text?.Text is null || text.Text.Length is < 1 or > 3000) ||
            command is not null && (command.Arguments is null || command.Arguments.Length > 4000 ||
                !handler.Handles(command.Name) && command.Name != "cancel")) return new("unsupported_payload");
        if (command?.Name is "learn_invite_later" or "learn_invite_skip")
        {
            var invitationReply = await handler.HandleAsync(new(command.Name, command.Arguments, request.UserId), token, inRussian: russian);
            session.EnsureComplete();
            return new("reply_committed", invitationReply);
        }
        var program = await store.ReadProgramAsync(request.UserId, token);
        var now = await NowAsync(db, token);
        var pedagogicalStamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            TargetStamp(db, includeDialogue: false) + JsonSerializer.Serialize(program, JsonSerializerOptions.Web))));
        var turn = new DialogueTurn(request.Id, request.UserId, command is null ? null : CommandKind(command.Name),
            command?.Arguments ?? text!.Text, command?.LessonId ?? text?.LessonId, command?.PromptId ?? text?.PromptId,
            new DateTimeOffset(request.ReceivedAt), now, lesson is null ? null : db.Entry(lesson).Property<uint>("TargetVersion").OriginalValue,
            actionInput is null ? request.SourceMessageId ?? request.SourceUpdateId : 0, actionInput?.Action, request.Sequence);
        var decisionStarted = Stopwatch.GetTimestamp();
        var decision = await new LearningDialogueService(store, session, processing.ConsultationEnabled || text?.ConsultationAllowed == true,
            processing.PracticeEnabled || text?.PracticeAllowed == true,
            processing.ProgramSwitchEnabled || text?.ProgramSwitchAllowed == true || command?.ProgramSwitchAllowed == true,
            services.GetRequiredService<LearningSessionService>())
            .DecideAsync(turn, lesson, program, pedagogicalStamp, token);
        var introduce = db.Entry(decision.Context).State == EntityState.Added;
        var audit = decision.Audit is null ? null : JsonSerializer.Serialize(decision.Audit, JsonSerializerOptions.Web);
        var diagnostics = services.GetService<DiagnosticJournal>();
        diagnostics?.Write("dialogue", "dialogue_interpreted", new(Status: decision.Audit?.Code ?? decision.Reply?.Kind.ToString(),
            Kind: decision.Execute?.ToString(), DurationMs: (long)Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds),
            entity: decision.Context.TargetLessonId, detail: () =>
            {
                var filter = services.GetService<DiagnosticText>();
                return new
                {
                    decision.Context.Phase, decision.Context.ExpectedResponse, decision.Context.Revision, decision.Context.PromptId,
                    originalRequestId = decision.Audit?.OriginalRequestId,
                    consultation = decision.Creation is { } draft ? new
                    {
                        goal = filter?.Filter(draft.Goal), practicalUse = filter?.Filter(draft.PracticalUse),
                        criterion = filter?.Filter(draft.GoalCriterion), source = "consultation_user_input"
                    } : null,
                    arguments = filter?.Filter(decision.Arguments), audit = filter?.Json(audit)
                };
            });
        if (decision.Creation is { } creation)
        {
            var result = await CreateCurriculum(services, session).CreateAsync(request.UserId, creation.Goal, creation.Experience, null, token,
                processing.EnglishDiagnosticEnabled || text?.EnglishDiagnosticAllowed == true, goalCriterion: creation.GoalCriterion,
                subjectDiagnostic: true, consultationUse: creation.PracticalUse);
            session.EnsureComplete();
            var completed = FirstGoalConsultation.CompleteCreation(decision.Context, result, turn);
            return new(completed.Audit!.Code)
                { Replies = Introduce(introduce, TelegramDialoguePresenter.Render(completed.Reply!, russian),
                    russian is null ? completed.Reply!.InEnglish : russian == false) };
        }
        if (decision.Execute is not { } operation)
        {
            var reply = decision.Reply ?? new(DialogueReplyKind.Unavailable);
            var dialogueReplies = TelegramDialoguePresenter.Render(reply, russian);
            if (decision.SuggestReviewsOnReturn && reply.Kind == DialogueReplyKind.Recap)
            {
                var suggestions = await CreateCurriculum(services, session).SuggestReviewsAsync(request.UserId, token);
                if (suggestions.FirstOrDefault() is { } suggestion)
                    dialogueReplies = [.. dialogueReplies, TelegramKnowledgePresenter.ReviewAfterResume(suggestion, russian ?? true)];
            }
            session.EnsureComplete();
            return new(decision.Audit?.Code ?? "dialogue_reply")
                { Replies = Introduce(introduce, dialogueReplies, russian is null ? reply.InEnglish : russian == false), DialogueOutcomeJson = audit };
        }
        if (operation == DialogueOperationKind.ReviewSuggestions || decision.SuggestReviewsOnReturn)
        {
            var suggestions = await CreateCurriculum(services, session).SuggestReviewsAsync(request.UserId, token);
            if (suggestions.Count > 0 || operation == DialogueOperationKind.ReviewSuggestions)
            {
                session.EnsureComplete();
                return new("review_suggestions") { Replies = Introduce(introduce, TelegramKnowledgePresenter.Suggestions(suggestions, russian ?? true), russian == false) };
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
            return new(result is CurriculumResult.RouteEditRejected ? "route_rejected" : "route_reply")
                { Replies = Introduce(introduce, TelegramCurriculumPresenter.Render(result, russian), russian == false), DialogueOutcomeJson = audit };
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
            var goalReplies = TelegramCurriculumPresenter.Render(result, russian);
            if (result is CurriculumResult.GoalUpdateOffered proposal)
            {
                var confirmation = LearningGoalChange.OpenUpdate(decision.Context, turn, program!, lesson, pedagogicalStamp, proposal);
                goalReplies = [.. goalReplies, .. TelegramDialoguePresenter.Render(new(DialogueReplyKind.ConfirmGoalUpdate,
                    [confirmation.Label!, confirmation.Code]), russian)];
                audit = JsonSerializer.Serialize(new DialogueAudit("confirmation_published", Confirmation: confirmation,
                    OriginalRequestId: turn.RequestId), JsonSerializerOptions.Web);
            }
            else if (operation == DialogueOperationKind.ApplyGoalUpdate)
                decision.Context.Synchronize(lesson, now, turn.SourceOrder);
            return new(result is CurriculumResult.GoalEditRejected ? "goal_update_rejected" : "goal_update_reply")
                { Replies = Introduce(introduce, goalReplies, russian == false), DialogueOutcomeJson = audit };
        }
        if (operation is DialogueOperationKind.ExplainAssessment or DialogueOperationKind.AppealAssessment)
        {
            var assessments = new AssessmentReviewService(services.GetRequiredService<IAssessmentReviewStore>(),
                services.GetRequiredService<IKnowledgeStore>(), session, services.GetRequiredService<TimeProvider>());
            var result = operation == DialogueOperationKind.AppealAssessment
                ? await assessments.ReviewAsync(request.UserId, request.Id, decision.Arguments, token)
                : await assessments.ExplainAsync(request.UserId, program?.Id, decision.Arguments, token);
            session.EnsureComplete();
            return new("assessment_review")
                { Replies = [.. TelegramAssessmentReviewPresenter.Render(result,
                    russian.HasValue ? !russian.Value : FirstGoalConsultation.InEnglish(lesson?.Theory ?? program?.Goal))] };
        }
        if (operation == DialogueOperationKind.PreviousTopic)
        {
            var previous = program is null ? null : await CreateCurriculum(services, session).PreviousTopicAsync(request.UserId,
                program.Id, lesson?.ProgramId == program.Id ? lesson.TopicCode : null, token);
            session.EnsureComplete();
            return new("previous_topic")
                { Replies = TelegramLearningPresenter.RenderPreviousTopic(previous, lesson,
                    russian.HasValue ? !russian.Value : FirstGoalConsultation.InEnglish(program?.Goal ?? lesson?.Theory)) };
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
            return new(result.Outcome) { Replies = Introduce(introduce, TelegramCurriculumPresenter.Render(result.Result, russian), russian == false) };
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
            var diagnosticRussian = russian ?? MarketLearningContent.InRussian(program?.Goal ?? turn.Text);
            return new(result is CurriculumResult.ProviderUnavailable ? "diagnostic_provider_unavailable" : "diagnostic_reply")
                { Replies = Introduce(introduce, TelegramCurriculumPresenter.Render(result, diagnosticRussian), !diagnosticRussian) };
        }
        if (operation is DialogueOperationKind.SwitchProgram or DialogueOperationKind.SwitchGoal or DialogueOperationKind.SwitchGoalWithCriterion or
            DialogueOperationKind.SwitchCreateProgram or DialogueOperationKind.SwitchCreateProjectProgram)
        {
            IReadOnlyList<TelegramLearningReply> switched;
            if (operation is DialogueOperationKind.SwitchGoal or DialogueOperationKind.SwitchGoalWithCriterion)
            {
                var goal = operation == DialogueOperationKind.SwitchGoalWithCriterion
                    ? decision.Arguments.Split('|', 2, StringSplitOptions.TrimEntries) : [decision.Arguments];
                var result = await CreateCurriculum(services, session).CreateAsync(request.UserId, goal[0],
                    "Опыт по новой цели не уточнён", null, token,
                    englishDiagnostic: processing.EnglishDiagnosticEnabled || text?.EnglishDiagnosticAllowed == true || program?.HasEnglishDiagnostic == true,
                    suspendCurrent: true, goalCriterion: goal.Length == 2 ? goal[1] : null);
                var reply = LearningGoalChange.Complete(decision.Context, result, turn);
                switched = result is CurriculumResult.Program ? TelegramDialoguePresenter.Render(reply, russian) : TelegramCurriculumPresenter.Render(result, russian);
            }
            else
            {
                var switchName = operation == DialogueOperationKind.SwitchProgram ? "learn_select" :
                    operation == DialogueOperationKind.SwitchCreateProgram ? "learn_request" : "learn_project";
                switched = await new TelegramCurriculumCommandHandler(CreateCurriculum(services, session))
                    .HandleAsync(new(switchName, decision.Arguments, request.UserId), token, suspendCurrent: true, inRussian: russian);
            }
            session.EnsureComplete();
            var restored = await services.GetRequiredService<ILessonStore>().FindActiveAsync(request.UserId, token);
            if (decision.Context.DiagnosticTarget is null) decision.Context.Synchronize(restored, now, turn.SourceOrder);
            if (restored is not null) switched = [.. switched, .. TelegramLearningPresenter.Render(LearningService.Session(restored), russian)];
            return new(decision.Audit?.Code ?? "program_switched") { Replies = switched, DialogueOutcomeJson = audit };
        }
        if (operation is DialogueOperationKind.BeginPractice or DialogueOperationKind.NextPractice or DialogueOperationKind.PausePractice or
            DialogueOperationKind.ResumePractice or DialogueOperationKind.StopPractice or DialogueOperationKind.PracticeInfo)
        {
            var result = await CreatePractice(services, session).ExecuteAsync(request.UserId, operation, decision.Arguments, token);
            session.EnsureComplete();
            var current = db.Set<Lesson>().Local.SingleOrDefault(value => value.UserId == request.UserId &&
                value.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused);
            decision.Context.Synchronize(current, now, turn.SourceOrder);
            return new("practice_reply") { Replies = TelegramPracticePresenter.Render(result, inRussian: russian) };
        }
        if (actionInput?.Action is { Kind: LearningActionKind.Hint or LearningActionKind.Solution or
            LearningActionKind.ExplainMaterial or LearningActionKind.ShowExample } help)
        {
            var result = await handler.ExecuteLessonAsync(new(CommandName(operation), decision.Arguments, request.UserId), token);
            session.EnsureComplete();
            var consumed = result is LearningResult.Explanation or LearningResult.ChoiceReply or LearningResult.Session;
            if (consumed) decision.Context.Synchronize(lesson, now, turn.SourceOrder);
            var helpRussian = russian ?? MarketLearningContent.InRussian(lesson?.Theory ?? program?.Goal ?? turn.Text);
            var helpReplies = TelegramLearningPresenter.Render(result, helpRussian);
            if (operation == DialogueOperationKind.RevealSolution && lesson?.Status == LessonStatus.Completed &&
                await CreatePractice(services, session).SynchronizeAsync(request.UserId, operation, token) is { } completedPractice)
                helpReplies = [.. helpReplies, .. TelegramPracticePresenter.Render(completedPractice, inRussian: russian)];
            if (result is LearningResult.ProviderUnavailable && decision.Context.Revision != help.Revision)
                helpReplies = [helpReplies.Single() with { Actions = [new(help.Kind)], InRussian = helpRussian }];
            return new("reply_committed", Introduce(introduce, helpReplies, russian == false)) { RemoveSourceKeyboard = consumed };
        }
        IReadOnlyList<TelegramLearningReply> replies;
        var languageIsRussian = russian ?? MarketLearningContent.InRussian(lesson?.Theory ?? program?.Goal ?? turn.Text);
        if (decision.ExerciseFormat is { } format)
        {
            var learning = new LearningService(services.GetRequiredService<ILessonStore>(), session, services.GetRequiredService<KnowledgeService>(),
                curriculum: services.GetRequiredService<ICurriculumStore>(), replyInRussian: russian);
            replies = TelegramLearningPresenter.Render(await learning.ChangeFormatAsync(request.UserId, format, token), languageIsRussian);
        }
        else if (operation is DialogueOperationKind.Clarify or DialogueOperationKind.Explain && lesson?.Status != LessonStatus.AwaitingAnswer)
        {
            var learning = new LearningService(services.GetRequiredService<ILessonStore>(), session, services.GetRequiredService<KnowledgeService>(),
                curriculum: services.GetRequiredService<ICurriculumStore>(), replyInRussian: russian);
            replies = TelegramLearningPresenter.Render(await learning.ExplainContextAsync(request.UserId, decision.Arguments, token), languageIsRussian);
        }
        else if (operation == DialogueOperationKind.Answer)
        {
            var result = await handler.ExecuteLessonAsync(new("answer", decision.Arguments, request.UserId), token);
            var followUp = await CreatePractice(services, session).AfterAnswerAsync(lesson, result, session, token);
            replies = TelegramLearningPresenter.RenderAnswerWithFollowUp(result, followUp, languageIsRussian, russian);
        }
        else if (operation == DialogueOperationKind.PracticeLastSkill)
        {
            var learning = new LearningService(services.GetRequiredService<ILessonStore>(), session, services.GetRequiredService<KnowledgeService>(),
                curriculum: services.GetRequiredService<ICurriculumStore>(), replyInRussian: russian);
            var practice = await learning.PracticeLastAsync(request.UserId, program?.Id, token);
            replies = practice is LearningResult.NoActiveLesson
                ? [languageIsRussian ? "Сначала откройте упражнение. После ответа можно попросить следующее задание по тому же навыку."
                    : "Open an exercise first. After answering, you can request another exercise on the same skill."]
                : TelegramLearningPresenter.Render(practice, russian);
        }
        else
        {
            var name = operation is DialogueOperationKind.None or DialogueOperationKind.StartOtherLesson ? command!.Name : CommandName(operation);
            replies = await handler.HandleAsync(new(name, decision.Arguments, request.UserId), token, program?.Discipline,
                languageIsRussian);
        }
        var practiceResult = operation == DialogueOperationKind.Answer ? null
            : await CreatePractice(services, session).SynchronizeAsync(request.UserId, operation, token);
        if (practiceResult is not null) replies = [.. replies, .. TelegramPracticePresenter.Render(practiceResult, inRussian: russian)];
        session.EnsureComplete();
        var active = db.Set<Lesson>().Local.SingleOrDefault(value => value.UserId == request.UserId &&
            value.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused);
        if (operation is not (DialogueOperationKind.Progress or DialogueOperationKind.PreferenceView or DialogueOperationKind.PreferenceSet or DialogueOperationKind.PreferenceReset))
            decision.Context.Synchronize(active, now, turn.SourceOrder);
        return new(decision.Audit?.Code ?? "reply_committed") { Replies = Introduce(introduce, replies, russian == false), DialogueOutcomeJson = audit };
    }

    private static IReadOnlyList<TelegramLearningReply> Introduce(bool introduce, IReadOnlyList<TelegramLearningReply> replies, bool english = false) => introduce
        ? [english ? TelegramDialoguePresenter.EnglishIntroduction : TelegramDialoguePresenter.Introduction, .. replies] : replies;

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
                        nameof(Lesson.RubricEvaluation) or nameof(Lesson.Choice) or
                        nameof(LearningEvidence.RubricEvidence) or nameof(LearningEvidence.ChoiceEvidence) or
                        nameof(LearningEvidence.ReviewHistory) or nameof(LearningEvidence.ReassessmentCredit) or nameof(CurriculumRoute.Path) or
                        nameof(LearningPreference.ResetThrough) or nameof(LessonInteraction.ExplanationPreferences)) || entry.OriginalValues[property] is not null)
                    .Where(property => entry.Entity is not Lesson || property.Name != nameof(Lesson.RecognitionOnly) ||
                        entry.OriginalValues[property] is true)
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
