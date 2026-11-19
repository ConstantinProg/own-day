using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OwnDay.Application.Dialogue;

public sealed class LearningDialogueService(IDialogueStore store, ILearningIntentResolver resolver, bool consultationEnabled = false,
    bool practiceEnabled = false, bool programSwitchEnabled = false, LearningSessionService? sessions = null)
{
    public async Task<DialogueDecision> DecideAsync(DialogueTurn turn, Lesson? lesson, DialogueProgramSnapshot? program,
        string stamp, CancellationToken token)
    {
        var context = await store.FindAsync(turn.UserId, token);
        var legacy = context is null;
        if (context is null)
        {
            context = DialogueContext.Create(turn.UserId, lesson, turn.Now);
            store.Add(context);
        }
        if (context.SchemaVersion != DialogueContext.CurrentSchemaVersion)
            return Reply(context, DialogueReplyKind.Unavailable);
        if (context.Consultation is { Version: not 1 }) return Reply(context, DialogueReplyKind.Unavailable);
        if (context.InterruptedAt is { } interrupted && turn.ReceivedAt < interrupted ||
            context.InterruptedSourceOrder > 0 && turn.SourceOrder <= context.InterruptedSourceOrder)
            return Reply(context, DialogueReplyKind.Interrupted);
        if (!context.MatchesQuestion(lesson) || context.PendingClarification is null && context.PendingConfirmation is null &&
            (lesson?.Status == LessonStatus.Paused && context.ExpectedResponse == DialogueExpectedResponse.LessonAnswer ||
             lesson?.Status == LessonStatus.AwaitingAnswer && context.Phase == DialoguePhase.Paused))
            context.Synchronize(lesson, turn.Now, turn.SourceOrder);

        var pendingConfirmation = context.PendingConfirmation;
        var currentConfirmationStamp = pendingConfirmation?.Operation is DialogueOperationKind.SelectProgram or DialogueOperationKind.SwitchProgram &&
            Guid.TryParse(pendingConfirmation.Arguments, out var pendingProgramId)
            ? WithProgramTarget(stamp, await store.ReadProgramByIdAsync(turn.UserId, pendingProgramId, token)) : stamp;
        if (pendingConfirmation is not null && (turn.Now >= pendingConfirmation.ExpiresAt || currentConfirmationStamp != pendingConfirmation.TargetStamp) ||
            context.PendingClarification is { } pendingChoice && (turn.Now >= pendingChoice.ExpiresAt || pendingChoice.TargetStamp != stamp))
            context.Synchronize(lesson, turn.Now, turn.SourceOrder);

        var normalized = Normalize(turn.Text);
        var command = turn.Command;
        if (command is DialogueOperationKind.ExplainAssessment or DialogueOperationKind.AppealAssessment)
            return new(context, command, turn.Text.Trim());
        if (command is null && normalized is "почему такая оценка" or "покажи оценку" or "оспорить оценку" or "explain the grade")
            return new(context, DialogueOperationKind.ExplainAssessment, "");
        const string appealPrefix = "оспорить оценку ";
        if (command is null && normalized.StartsWith(appealPrefix, StringComparison.Ordinal))
            return new(context, DialogueOperationKind.AppealAssessment, turn.Text.Trim()[appealPrefix.Length..]);
        if (command == DialogueOperationKind.ReviewSuggestions || command is null && normalized.TrimEnd('?') is "что повторить" or "what should i review")
            return new(context, DialogueOperationKind.ReviewSuggestions, turn.Text.Trim());
        var routeView = command == DialogueOperationKind.ViewRoute ? normalized : command is null ? RouteDialogue.View(normalized) : null;
        if (routeView is not null) return new(context, DialogueOperationKind.ViewRoute, routeView);
        var routeEdit = command == DialogueOperationKind.EditRoute ? turn.Text.Trim() : command is null ? RouteDialogue.Edit(normalized) : null;
        if (routeEdit is not null)
            return await DecideActionAsync(context, turn, DialogueOperationKind.EditRoute, routeEdit, lesson, program, stamp, false, false, token);
        const string goalUpdatePrefix = "обновить цель на ";
        const string englishGoalUpdatePrefix = "update goal to ";
        var goalUpdate = command == DialogueOperationKind.ProposeGoalUpdate ? turn.Text.Trim() : command is not null ? null :
            normalized.StartsWith(goalUpdatePrefix, StringComparison.Ordinal) ? turn.Text.Trim()[goalUpdatePrefix.Length..] :
            normalized.StartsWith(englishGoalUpdatePrefix, StringComparison.Ordinal) ? turn.Text.Trim()[englishGoalUpdatePrefix.Length..] : null;
        if (goalUpdate is not null)
        {
            if (program is null) return Reply(context, DialogueReplyKind.ProgramUnavailable);
            if (turn.SourceOrder <= context.ResponseAfterSourceOrder) return Reply(context, DialogueReplyKind.NeedBinding);
            return new(context, DialogueOperationKind.ProposeGoalUpdate, goalUpdate);
        }
        var returning = false;
        if (sessions is not null && turn.SourceOrder > 0)
        {
            if (command == DialogueOperationKind.EndSession ||
                command is null && normalized is "завершить сессию" or "закончить сессию" or "end session")
            {
                await sessions.EndAsync(turn, program?.Id, lesson, token);
                context.Synchronize(lesson, turn.Now, turn.SourceOrder);
                context.AdvanceResponseBoundary(turn.SourceOrder);
                return Reply(context, DialogueReplyKind.SessionEnded);
            }

            var sessionAction = command ?? Deterministic(normalized);
            if (program is not null && sessionAction is not (DialogueOperationKind.Progress or DialogueOperationKind.Finish or
                    DialogueOperationKind.Confirm or DialogueOperationKind.Cancel or DialogueOperationKind.None))
                returning = await sessions.RecordActivityAsync(turn, program.Id, lesson, token);
            if (returning && lesson is not null && sessionAction != DialogueOperationKind.Resume)
            {
                context.Synchronize(lesson, turn.Now, turn.SourceOrder);
                context.AdvanceResponseBoundary(turn.SourceOrder);
                return Recap(context, lesson);
            }
        }
        if (lesson is null && program is null && context.Consultation is { CreatedProgramId: null } &&
            command is DialogueOperationKind.Cancel or DialogueOperationKind.Pause or DialogueOperationKind.Resume)
            return await FirstGoalConsultation.DecideAsync(context, turn with
                { Command = null, Text = command == DialogueOperationKind.Resume ? "resume" : "pause" }, resolver, token);
        if (lesson is null && program is null && command is null && (consultationEnabled || context.Consultation is not null))
            return await FirstGoalConsultation.DecideAsync(context, turn, resolver, token);
        if (command == DialogueOperationKind.Cancel || command is null &&
            (normalized is "отмена" or "cancel" || normalized == "нет" && context.PendingConfirmation is not null))
        {
            context.Synchronize(lesson, turn.Now, turn.SourceOrder);
            return Reply(context, DialogueReplyKind.Cancelled);
        }
        var code = command == DialogueOperationKind.Confirm ? turn.Text.Trim()
            : normalized.StartsWith("подтверждаю ", StringComparison.Ordinal) ? normalized[12..].Trim() : null;
        if (code is not null)
        {
            var expected = context.PendingConfirmation;
            var confirmedProposal = context.ConsumeConfirmation(code, turn.ReceivedAt, currentConfirmationStamp, turn.Now, turn.SourceOrder);
            if (confirmedProposal is null)
            {
                if (expected is not null && (turn.Now >= expected.ExpiresAt || currentConfirmationStamp != expected.TargetStamp))
                    context.Synchronize(lesson, turn.Now, turn.SourceOrder);
                return Reply(context, DialogueReplyKind.ConfirmationRejected)
                    with { Audit = new("confirmation_rejected", Confirmation: expected ?? pendingConfirmation) };
            }
            var resolved = await DecideActionAsync(context, turn with { RequestId = confirmedProposal.OriginalRequestId }, confirmedProposal.Operation,
                confirmedProposal.Arguments, lesson, program, stamp, confirmed: true, bound: true, token);
            return resolved with { Audit = new("confirmation_resolved", Confirmation: confirmedProposal,
                OriginalRequestId: confirmedProposal.OriginalRequestId, ChosenAction: confirmedProposal.Operation) };
        }
        if (context.PendingConfirmation is not null && command is null && Deterministic(normalized) is DialogueOperationKind.Pause or DialogueOperationKind.Resume)
            context.Synchronize(lesson, turn.Now);
        if (context.PendingConfirmation is { } confirmation && command is null)
            return ConfirmationReply(context, confirmation);

        if (context.PendingClarification is { } clarification && command is null)
        {
            if (turn.ReceiptPromptId != clarification.PromptId)
                return Reply(context, DialogueReplyKind.ChoiceUnbound);
            var choice = Choice(normalized);
            if (choice is null || !clarification.Allowed.Contains(choice.Value)) return ChoiceReply(context, clarification.Allowed);
            var originalId = context.ConsumeClarification(choice.Value, turn.ReceivedAt, stamp, turn.Now, turn.SourceOrder);
            if (originalId is null)
            {
                if (turn.Now >= clarification.ExpiresAt || clarification.TargetStamp != stamp) context.Synchronize(lesson, turn.Now, turn.SourceOrder);
                return Reply(context, DialogueReplyKind.ChoiceExpired);
            }
            var original = await store.ReadOriginalAsync(turn.UserId, originalId.Value, token);
            if (original is null || original.LessonId != lesson?.Id || original.PromptId is { } originalPrompt && originalPrompt != context.PromptId)
                return Reply(context, DialogueReplyKind.OriginalStale);
            var resolved = await DecideActionAsync(context, turn with { RequestId = originalId.Value }, choice.Value, original.Text,
                lesson, program, stamp, confirmed: false, bound: true, token);
            return resolved with { Audit = (resolved.Audit ?? new("clarification_resolved")) with
                { Clarification = clarification, OriginalRequestId = originalId.Value, ChosenAction = choice.Value } };
        }
        if (context.PendingClarification is not null && command == DialogueOperationKind.Answer)
            return ChoiceReply(context, context.PendingClarification.Allowed);
        if (command is not null && (context.PendingClarification is not null || context.PendingConfirmation is not null))
            context.Synchronize(lesson, turn.Now, turn.SourceOrder);

        if (programSwitchEnabled && program is not null && command is null)
        {
            const string prefix = "сменить цель на ";
            const string englishPrefix = "change goal to ";
            const string selectPrefix = "выбрать программу ";
            const string russianSwitchPrefix = "переключись на ";
            const string englishSwitchPrefix = "switch to ";
            var newGoalPrefix = normalized.StartsWith(prefix, StringComparison.Ordinal) ? prefix :
                normalized.StartsWith(englishPrefix, StringComparison.Ordinal) ? englishPrefix : null;
            if (newGoalPrefix is not null)
            {
                var goal = turn.Text.Trim()[newGoalPrefix.Length..].Trim();
                var parts = goal.Split('|', 2, StringSplitOptions.TrimEntries);
                if (parts[0].Length is < 1 or > LearningProgram.MaxGoalLength || parts.Length == 2 &&
                    parts[1].Length is < 1 or > LearningProgram.MaxGoalCriterionLength) return Reply(context, DialogueReplyKind.AskNewGoal);
                var goalOperation = parts.Length == 2 ? DialogueOperationKind.SwitchGoalWithCriterion : DialogueOperationKind.SwitchGoal;
                return await DecideActionAsync(context, turn, goalOperation, goal, lesson, program, stamp, false, true, token);
            }
            var programPrefix = normalized.StartsWith(selectPrefix, StringComparison.Ordinal) ? selectPrefix :
                normalized.StartsWith(russianSwitchPrefix, StringComparison.Ordinal) ? russianSwitchPrefix :
                normalized.StartsWith(englishSwitchPrefix, StringComparison.Ordinal) ? englishSwitchPrefix : null;
            if (programPrefix is not null)
            {
                var query = turn.Text.Trim()[programPrefix.Length..].Trim().TrimEnd('.', '!');
                var id = Guid.TryParse(query, out var parsed) ? parsed : (Guid?)null;
                if (id is null)
                {
                    if (query.Length is < 1 or > LearningProgram.MaxGoalLength) return Reply(context, DialogueReplyKind.ProgramUnavailable);
                    var candidates = (await store.ListProgramsAsync(turn.UserId, token)).Where(item =>
                        item.Goal.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Discipline.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(item => item.Goal, StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
                    if (candidates.Length != 1)
                        return candidates.Length == 0 ? Reply(context, DialogueReplyKind.ProgramUnavailable) : new(context, null, "",
                            new(DialogueReplyKind.ChooseMatchingProgram, candidates.Take(5).Select(item =>
                                $"{item.Goal} ({item.Discipline})\n/learn_select {item.Id}").ToArray()));
                    id = candidates[0].Id;
                }
                return await DecideActionAsync(context, turn, DialogueOperationKind.SelectProgram, id.Value.ToString(),
                    lesson, program, stamp, false, true, token);
            }
        }

        if (practiceEnabled || program?.PracticeStatus is not null)
        {
            var count = normalized switch { "давай одно упражнение" or "практика 1" => "1",
                "давай два упражнения" or "практика 2" => "2", "давай три упражнения" or "практика 3" => "3", _ => null };
            if (command is null && count is not null) return PracticeAction(context, turn, DialogueOperationKind.BeginPractice, count);
            if (command is null && (normalized.StartsWith("практика ", StringComparison.Ordinal) ||
                normalized.StartsWith("давай ", StringComparison.Ordinal) && normalized.Contains("упражнен", StringComparison.Ordinal)))
                return PracticeAction(context, turn, DialogueOperationKind.BeginPractice, "0");
        }
        if (program?.PracticeStatus is not null)
        {
            if (lesson is null && command is null && normalized is "помощь" or "help" or "что дальше" or "как продолжить")
                return new(context, DialogueOperationKind.PracticeInfo, "");
            if (command == DialogueOperationKind.Next || command is null && normalized is "дальше" or "следующее упражнение" or "next")
                return PracticeAction(context, turn, DialogueOperationKind.NextPractice);
            if (program.PracticeStatus is LearningPracticeStatus.Running or LearningPracticeStatus.Paused)
            {
                if (command == DialogueOperationKind.Pause || command is null && normalized is "пауза" or "pause" or "мне пора, продолжим позже")
                    return PracticeAction(context, turn, DialogueOperationKind.PausePractice);
                if (command is null && normalized is "хватит практики" or "закончим практику") return PracticeAction(context, turn, DialogueOperationKind.StopPractice);
                if (command == DialogueOperationKind.Resume || command is null && normalized is "продолжим" or "продолжить" or "resume")
                    return PracticeAction(context, turn, DialogueOperationKind.ResumePractice);
            }
        }

        if (lesson is null && program?.HasEnglishDiagnostic == true &&
            command is null or DialogueOperationKind.Diagnose or DialogueOperationKind.Resume or DialogueOperationKind.Next or DialogueOperationKind.Pause or DialogueOperationKind.Cancel)
            return new(context, DialogueOperationKind.Diagnose,
                command == DialogueOperationKind.Resume ? "resume" : command == DialogueOperationKind.Next ? "start" :
                command is DialogueOperationKind.Pause or DialogueOperationKind.Cancel ? "pause" : turn.Text);

        if (lesson is null && program is { HasSubjectDiagnostic: true })
        {
            if (command == DialogueOperationKind.Diagnose)
            {
                var arguments = program.PendingDiagnosticQuestion is { } currentQuestion && normalized is not ("start" or "skip") &&
                    !IsDiagnosticAnswerBound(context, turn, program.Id, currentQuestion) ? "start" : turn.Text;
                return new(context, DialogueOperationKind.CompleteDiagnostic, arguments);
            }
            if (command is null && normalized is "начать диагностику" or "вернуться к диагностике" or "start diagnostic")
                return new(context, DialogueOperationKind.CompleteDiagnostic, "start");
        }
        if (lesson is null && program is { PendingDiagnosticQuestion: { } question } && command is null)
            return DecideDiagnostic(context, turn, normalized, program, question);
        if (lesson is null && program is not null && (command == DialogueOperationKind.Resume ||
            command is null && Deterministic(normalized) == DialogueOperationKind.Resume))
            return new(context, DialogueOperationKind.Next, "") { SuggestReviewsOnReturn = returning && program.PendingDiagnosticQuestion is null };

        var bound = lesson is not null && turn.ReceiptLessonId == lesson.Id &&
            (turn.ReceiptPromptId == context.PromptId || legacy && turn.ReceiptPromptId is null) &&
            (context.ResponseAfterSourceOrder == 0 || turn.SourceOrder > context.ResponseAfterSourceOrder);
        if (command is { } explicitAction)
            return await DecideActionAsync(context, turn, explicitAction, turn.Text, lesson, program, stamp, false, bound, token);

        if (normalized is "помощь" or "help" or "что дальше" or "как продолжить")
            return lesson is null ? Reply(context, DialogueReplyKind.NoActiveLesson) : Recap(context, lesson);

        if ((normalized.Contains("пауза", StringComparison.Ordinal) || normalized.Contains("pause", StringComparison.Ordinal)) &&
            (normalized.Contains("продолж", StringComparison.Ordinal) || normalized.Contains("resume", StringComparison.Ordinal)))
            return OpenChoice(context, turn, stamp, [DialogueOperationKind.Pause, DialogueOperationKind.Resume]);
        if (normalized is "другая тема" or "другая цель" or "another topic" or "another goal")
            return OpenChoice(context, turn, stamp, [DialogueOperationKind.Clarify, DialogueOperationKind.ChangeGoal]);
        var deterministic = Deterministic(normalized);
        if (deterministic != DialogueOperationKind.Clarify &&
            (normalized.Contains("объясни", StringComparison.Ordinal) || normalized.Contains("поясни", StringComparison.Ordinal) ||
             normalized.Contains("это правильно", StringComparison.Ordinal)))
            return OpenChoice(context, turn, stamp, [DialogueOperationKind.Answer, DialogueOperationKind.Clarify]);
        if (deterministic is { } action)
            return await DecideActionAsync(context, turn, action, turn.Text, lesson, program, stamp, false, bound, token);
        if (lesson?.Status != LessonStatus.AwaitingAnswer)
            return Reply(context, lesson is null ? DialogueReplyKind.NoActiveLesson : DialogueReplyKind.Paused);
        if (!bound) return Reply(context, DialogueReplyKind.NeedBinding);
        if (normalized is "давай" or "пропустить" or "пропустим" or "позже" or "skip" or "later")
            return Recap(context, lesson);
        if (normalized is "ответ" or "вопрос" or "answer" or "question")
            return Reply(context, DialogueReplyKind.NeedActualInput);
        if (normalized is "yes" or "no" && (lesson.ResponseQuestion.Contains("yes or no", StringComparison.OrdinalIgnoreCase) ||
            lesson.ResponseQuestion.Contains("yes/no", StringComparison.OrdinalIgnoreCase)))
            return await DecideActionAsync(context, turn, DialogueOperationKind.Answer, turn.Text, lesson, program, stamp, false, bound, token);
        if (normalized is "да" or "нет" or "yes" or "no" or "потом" or "не знаю" ||
            normalized.Contains("ignore previous", StringComparison.Ordinal) || normalized.Contains("ignore system", StringComparison.Ordinal))
            return OpenChoice(context, turn, stamp, [DialogueOperationKind.Answer, DialogueOperationKind.Clarify]);
        var allowed = new[] { LearningIntent.Ambiguous, LearningIntent.Answer, LearningIntent.Clarify,
            LearningIntent.Pause, LearningIntent.Resume, LearningIntent.Progress, LearningIntent.Next, LearningIntent.GoalChange };
        LearningIntentProposal proposal;
        try
        {
            proposal = await resolver.ResolveAsync(new(turn.Text, lesson.ResponseQuestion, program?.Goal, program?.Discipline,
                lesson.Interactions.OrderByDescending(interaction => interaction.Id).Take(3)
                    .Select(interaction => interaction.Explanation[..Math.Min(500, interaction.Explanation.Length)]).ToArray(), allowed,
                program?.Id, program?.RouteVersion, program?.Version, context.Revision, context.PromptId), token);
        }
        catch (Learning.LessonProviderException)
        {
            return OpenChoice(context, turn, stamp, [DialogueOperationKind.Answer, DialogueOperationKind.Clarify]);
        }
        if (!allowed.Contains(proposal.Intent) || proposal.Intent == LearningIntent.Ambiguous)
            return OpenChoice(context, turn, stamp, [DialogueOperationKind.Answer, DialogueOperationKind.Clarify]);
        var proposedAction = proposal.Intent switch
        {
            LearningIntent.Answer => DialogueOperationKind.Answer,
            LearningIntent.Clarify => DialogueOperationKind.Clarify,
            LearningIntent.Pause => DialogueOperationKind.Pause,
            LearningIntent.Resume => DialogueOperationKind.Resume,
            LearningIntent.Progress => DialogueOperationKind.Progress,
            LearningIntent.Next => DialogueOperationKind.Next,
            _ => DialogueOperationKind.ChangeGoal
        };
        return await DecideActionAsync(context, turn, proposedAction, turn.Text, lesson, program, stamp, false, bound, token);
    }

    private static DialogueDecision DecideDiagnostic(DialogueContext context, DialogueTurn turn, string normalized, DialogueProgramSnapshot program, string question)
    {
        var action = Deterministic(normalized);
        if (action == DialogueOperationKind.Progress) return new(context, action, "");

        var answering = context.MatchesDiagnosticQuestion(program.Id, question);
        var offer = context.Phase == DialoguePhase.DiagnosticOffer && context.Consultation?.CreatedProgramId == program.Id;
        var english = FirstGoalConsultation.InEnglish(program.Goal);
        var boundary = offer ? context.Consultation!.AfterSourceOrder : context.ResponseAfterSourceOrder;
        var bound = turn.ReceiptPromptId == context.PromptId && turn.SourceOrder > boundary &&
            turn.ReceivedAt >= context.PromptOpenedAt;
        var skip = normalized is "пропустить" or "пропустим" or "позже" or "потом" or "skip" or "later" ||
            action == DialogueOperationKind.Next;
        if (skip && (bound || !answering && !offer))
            return new(context, DialogueOperationKind.CompleteDiagnostic, "skip");

        if (action == DialogueOperationKind.Resume ||
            (normalized is "давай" or "да" or "yes" or "start" or "начнём диагностику") && (bound || !offer))
        {
            if (!answering) context.BindDiagnosticQuestion(program.Id, question, turn.Now, turn.SourceOrder);
            return new(context, null, "", new(DialogueReplyKind.DiagnosticQuestion, [question], english));
        }
        if (!answering) return new(context, null, "", new(DialogueReplyKind.DiagnosticOffer, InEnglish: english));
        if (bound && !skip && action is null && normalized is not ("нет" or "no" or "помощь" or "help") &&
            !turn.Text.TrimEnd().EndsWith('?'))
            return new(context, DialogueOperationKind.CompleteDiagnostic, turn.Text);
        return new(context, null, "", new(DialogueReplyKind.DiagnosticQuestion, [question], english));
    }

    private static bool IsDiagnosticAnswerBound(DialogueContext context, DialogueTurn turn, Guid programId, string question) =>
        context.MatchesDiagnosticQuestion(programId, question) && turn.ReceiptPromptId == context.PromptId &&
        turn.SourceOrder > context.ResponseAfterSourceOrder && turn.ReceivedAt >= context.PromptOpenedAt;

    private async Task<DialogueDecision> DecideActionAsync(DialogueContext context, DialogueTurn turn, DialogueOperationKind action,
        string arguments, Lesson? lesson, DialogueProgramSnapshot? program, string stamp, bool confirmed, bool bound, CancellationToken token)
    {
        if (action is DialogueOperationKind.Answer or DialogueOperationKind.Clarify or DialogueOperationKind.Explain or DialogueOperationKind.RevealSolution)
        {
            if (!bound || lesson?.Status != LessonStatus.AwaitingAnswer)
                return Reply(context, DialogueReplyKind.NeedBinding);
        }
        if (action == DialogueOperationKind.Resume && lesson?.Status == LessonStatus.AwaitingAnswer)
        {
            context.Synchronize(lesson, turn.Now, turn.SourceOrder);
            return Recap(context, lesson);
        }
        if (action == DialogueOperationKind.StartOtherLesson && lesson is null &&
            program?.PracticeStatus is LearningPracticeStatus.Running or LearningPracticeStatus.Paused)
            return Reply(context, DialogueReplyKind.MustEndPractice);
        DialogueProgramSnapshot? selectedProgram = null;
        if (!confirmed && programSwitchEnabled && program is not null)
            action = action switch
            {
                DialogueOperationKind.SelectProgram => DialogueOperationKind.SwitchProgram,
                DialogueOperationKind.CreateProgram => DialogueOperationKind.SwitchCreateProgram,
                DialogueOperationKind.CreateProjectProgram => DialogueOperationKind.SwitchCreateProjectProgram,
                _ => action
            };
        if (action is DialogueOperationKind.SwitchProgram or DialogueOperationKind.SwitchGoal or DialogueOperationKind.SwitchGoalWithCriterion or
            DialogueOperationKind.SwitchCreateProgram or DialogueOperationKind.SwitchCreateProjectProgram)
        {
            if (!confirmed && turn.SourceOrder <= context.ResponseAfterSourceOrder) return Reply(context, DialogueReplyKind.NeedBinding);
        }
        if (action is DialogueOperationKind.CreateProgram or DialogueOperationKind.CreateProjectProgram or DialogueOperationKind.SelectProgram or DialogueOperationKind.SwitchProgram)
        {
            if (lesson is not null && action != DialogueOperationKind.SwitchProgram) return Reply(context, DialogueReplyKind.MustFinishLesson);
            if (action is DialogueOperationKind.SelectProgram or DialogueOperationKind.SwitchProgram)
            {
                if (Guid.TryParse(arguments.Trim(), out var target)) selectedProgram = await store.ReadProgramByIdAsync(turn.UserId, target, token);
                if (selectedProgram is null) return Reply(context, DialogueReplyKind.ProgramUnavailable);
            }
        }
        if (action == DialogueOperationKind.ChangeGoal && programSwitchEnabled) return Reply(context, DialogueReplyKind.AskNewGoal);
        string? routeEditLabel = null;
        if (action == DialogueOperationKind.EditRoute)
        {
            if (program is null) return Reply(context, DialogueReplyKind.ProgramUnavailable);
            if (!confirmed && turn.SourceOrder <= context.ResponseAfterSourceOrder) return Reply(context, DialogueReplyKind.NeedBinding);
            routeEditLabel = RouteDialogue.DescribeEdit(arguments);
            if (routeEditLabel is null) return Reply(context, DialogueReplyKind.RouteEditHelp);
        }
        var consequential = action == DialogueOperationKind.Finish && lesson is not null || action is DialogueOperationKind.ChangeGoal or DialogueOperationKind.EditRoute ||
            action is DialogueOperationKind.CreateProgram or DialogueOperationKind.CreateProjectProgram && program is not null ||
            action == DialogueOperationKind.SelectProgram && Guid.TryParse(arguments.Trim(), out var selected) && selected != program?.Id ||
            action is DialogueOperationKind.SwitchProgram or DialogueOperationKind.SwitchGoal or DialogueOperationKind.SwitchGoalWithCriterion or
                DialogueOperationKind.SwitchCreateProgram or DialogueOperationKind.SwitchCreateProjectProgram;
        if (consequential && !confirmed)
        {
            var targetProgram = selectedProgram ?? program;
            if (action is DialogueOperationKind.SelectProgram or DialogueOperationKind.SwitchProgram && targetProgram is null) return Reply(context, DialogueReplyKind.ProgramUnavailable);
            var confirmationStamp = action is DialogueOperationKind.SelectProgram or DialogueOperationKind.SwitchProgram ? WithProgramTarget(stamp, targetProgram) : stamp;
            var label = action is DialogueOperationKind.SelectProgram or DialogueOperationKind.SwitchProgram ? Bounded(targetProgram!.Goal)
                : action == DialogueOperationKind.Finish ? Bounded(lesson!.Topic) : routeEditLabel ?? Bounded(arguments);
            context.OpenConfirmation(turn.RequestId, action, arguments, confirmationStamp, turn.Now,
                new(targetProgram?.Id, targetProgram?.RouteVersion, lesson?.Id, context.QuestionFingerprint,
                    targetProgram?.Version, turn.LessonVersion), label, turn.SourceOrder);
            return ConfirmationReply(context, context.PendingConfirmation!) with
                { Audit = new("confirmation_published", Confirmation: context.PendingConfirmation, OriginalRequestId: turn.RequestId) };
        }
        if (action == DialogueOperationKind.ChangeGoal)
        {
            if (programSwitchEnabled) return Reply(context, DialogueReplyKind.AskNewGoal);
            context.DeferGoal(turn.RequestId, turn.Now);
            context.Synchronize(lesson, turn.Now, turn.SourceOrder);
            return Reply(context, DialogueReplyKind.GoalDeferred);
        }
        var actualArguments = turn.Command is null && action is DialogueOperationKind.Pause or DialogueOperationKind.Resume or
            DialogueOperationKind.Finish or DialogueOperationKind.Next or DialogueOperationKind.Progress or DialogueOperationKind.Explain ? "" : arguments;
        return new(context, action, actualArguments);
    }

    private static DialogueDecision OpenChoice(DialogueContext context, DialogueTurn turn, string stamp, DialogueOperationKind[] allowed)
    {
        context.OpenClarification(turn.RequestId, allowed, stamp, turn.Now, turn.SourceOrder);
        return ChoiceReply(context, allowed) with
            { Audit = new("clarification_published", Clarification: context.PendingClarification, OriginalRequestId: turn.RequestId) };
    }
    private static DialogueDecision ChoiceReply(DialogueContext context, IReadOnlyCollection<DialogueOperationKind> allowed) =>
        Reply(context, allowed.Contains(DialogueOperationKind.Answer)
            ? DialogueReplyKind.ChooseAnswerOrQuestion : allowed.Contains(DialogueOperationKind.ChangeGoal)
                ? DialogueReplyKind.ChooseQuestionOrGoal : DialogueReplyKind.ChoosePauseOrResume);
    private static DialogueDecision ConfirmationReply(DialogueContext context, DialogueConfirmation proposal) => new(context, null, "",
        new(proposal.Operation switch
        {
            DialogueOperationKind.Finish => DialogueReplyKind.ConfirmFinish,
            DialogueOperationKind.SelectProgram => DialogueReplyKind.ConfirmProgramSelection,
            DialogueOperationKind.SwitchProgram => DialogueReplyKind.ConfirmProgramSwitch,
            DialogueOperationKind.SwitchGoal or DialogueOperationKind.SwitchGoalWithCriterion or DialogueOperationKind.SwitchCreateProgram or
                DialogueOperationKind.SwitchCreateProjectProgram => DialogueReplyKind.ConfirmGoalSwitch,
            DialogueOperationKind.ChangeGoal => DialogueReplyKind.ConfirmGoalIntent,
            DialogueOperationKind.EditRoute => DialogueReplyKind.ConfirmRouteEdit,
            DialogueOperationKind.ApplyGoalUpdate => DialogueReplyKind.ConfirmGoalUpdate,
            _ => DialogueReplyKind.ConfirmProgramCreation
        }, [proposal.Label ?? "", proposal.Code]));
    private static DialogueDecision Reply(DialogueContext context, DialogueReplyKind kind) => new(context, null, "", new(kind));
    private static DialogueDecision PracticeAction(DialogueContext context, DialogueTurn turn, DialogueOperationKind operation, string arguments = "")
    {
        if (turn.SourceOrder <= context.ResponseAfterSourceOrder) return Reply(context, DialogueReplyKind.NeedBinding);
        context.AdvanceResponseBoundary(turn.SourceOrder);
        return new(context, operation, arguments);
    }
    private static DialogueDecision Recap(DialogueContext context, Lesson lesson) => new(context, null, "", new(DialogueReplyKind.Recap,
        [lesson.Topic, lesson.Theory, lesson.ResponseQuestion, lesson.SavedGuidance ?? "", lesson.Status.ToString()]));
    private static string Normalize(string text) => text.Trim().TrimEnd('.', '!').ToLowerInvariant();
    private static string Bounded(string text) => text[..Math.Min(500, text.Length)];
    private static string WithProgramTarget(string stamp, DialogueProgramSnapshot? program) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp + JsonSerializer.Serialize(program, JsonSerializerOptions.Web))));
    private static DialogueOperationKind? Choice(string text) => text switch
    {
        "ответ" or "answer" => DialogueOperationKind.Answer,
        "вопрос" or "уточнение" or "question" => DialogueOperationKind.Clarify,
        "пауза" or "pause" => DialogueOperationKind.Pause,
        "продолжить" or "продолжим" or "resume" => DialogueOperationKind.Resume,
        "цель" or "сменить цель" or "goal" => DialogueOperationKind.ChangeGoal,
        _ => null
    };
    private static DialogueOperationKind? Deterministic(string text)
    {
        if (text is "пауза" or "поставь на паузу" or "мне пора, продолжим позже" or "продолжим позже" or "pause" or "stop") return DialogueOperationKind.Pause;
        if (text is "продолжим" or "продолжить" or "давай продолжим" or "resume" or "continue") return DialogueOperationKind.Resume;
        if (text is "прогресс" or "мой прогресс" or "покажи прогресс" or "progress") return DialogueOperationKind.Progress;
        if (text is "дальше" or "следующее упражнение" or "next") return DialogueOperationKind.Next;
        if (text is "завершить упражнение" or "закончу занятие" or "finish") return DialogueOperationKind.Finish;
        if (text is "покажи решение" or "покажи полное решение" or "дай полный ответ" or "show the solution")
            return DialogueOperationKind.RevealSolution;
        if (text is "дай подсказку" or "подскажи" or "hint") return DialogueOperationKind.Clarify;
        if (text.StartsWith("объясни", StringComparison.Ordinal) || text.StartsWith("поясни", StringComparison.Ordinal) ||
            text.StartsWith("объясняй ", StringComparison.Ordinal) || text.StartsWith("explain in ", StringComparison.Ordinal) ||
            text.StartsWith("не понял", StringComparison.Ordinal) || text.StartsWith("не поняла", StringComparison.Ordinal) ||
            text.StartsWith("почему здесь", StringComparison.Ordinal) || text.StartsWith("не знаю, объясни", StringComparison.Ordinal)) return DialogueOperationKind.Clarify;
        if (text.StartsWith("хочу другую цель", StringComparison.Ordinal) || text.StartsWith("сменить цель", StringComparison.Ordinal)) return DialogueOperationKind.ChangeGoal;
        return null;
    }
}
