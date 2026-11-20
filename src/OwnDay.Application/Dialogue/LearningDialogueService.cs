using OwnDay.Application.Learning;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OwnDay.Application.Dialogue;

public sealed class LearningDialogueService(IDialogueStore store, ILearningIntentResolver resolver, bool consultationEnabled = false,
    bool practiceEnabled = false, bool programSwitchEnabled = false, LearningSessionService? sessions = null)
{
    public async Task<ChoiceApplication> ApplyChoiceAsync(DialogueTurn turn, Lesson? lesson, ChoiceInput input,
        LearningService learning, CancellationToken token)
    {
        if (lesson is not null && lesson.UserId == turn.UserId && input.Matches(lesson) && sessions is not null &&
            await store.ReadProgramAsync(turn.UserId, token) is { } program &&
            await sessions.RecordActivityAsync(turn, program.Id, lesson, token))
        {
            var context = await GetChoiceContextAsync(turn, lesson, token);
            RestoreCurrentExercise(context, turn, lesson);
            return new(LearningService.Session(lesson));
        }

        var result = await learning.ApplyChoiceAsync(turn.UserId, lesson, input, token);
        if (result.AttemptRecorded)
        {
            var context = await GetChoiceContextAsync(turn, lesson, token);
            context.Synchronize(lesson, turn.Now, turn.SourceOrder);
        }
        return result;
    }

    private async Task<DialogueContext> GetChoiceContextAsync(DialogueTurn turn, Lesson? lesson, CancellationToken token)
    {
        var context = await store.FindAsync(turn.UserId, token);
        if (context is not null) return context;
        context = DialogueContext.Create(turn.UserId, lesson, turn.Now);
        store.Add(context);
        return context;
    }

    private static void RestoreCurrentExercise(DialogueContext context, DialogueTurn turn, Lesson lesson)
    {
        lesson.RefreshChoicePrompt();
        context.Synchronize(lesson, turn.Now, turn.SourceOrder);
        context.AdvanceResponseBoundary(turn.SourceOrder);
    }

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
        var naturalAction = command is null ? Deterministic(normalized) : null;
        if (command is null && lesson is null && program is not null && normalized is "давай" or "let's go")
            naturalAction = DialogueOperationKind.Resume;
        var english = FirstGoalConsultation.InEnglish(lesson?.Theory ?? program?.Goal);
        var anotherExercise = command is null && normalized is "ещё задание" or "еще задание" or
            "ещё упражнение" or "еще упражнение" or "another exercise";
        if (command is DialogueOperationKind.ExplainAssessment or DialogueOperationKind.AppealAssessment)
            return new(context, command, turn.Text.Trim());
        if (command is null && normalized is "почему такая оценка" or "покажи оценку" or "оспорить оценку" or "explain the grade")
            return new(context, DialogueOperationKind.ExplainAssessment, "");
        const string appealPrefix = "оспорить оценку ";
        if (command is null && normalized.StartsWith(appealPrefix, StringComparison.Ordinal))
            return new(context, DialogueOperationKind.AppealAssessment, turn.Text.Trim()[appealPrefix.Length..]);
        if (command == DialogueOperationKind.ReviewSuggestions || command is null && normalized.TrimEnd('?') is "что повторить" or "what should i review")
            return new(context, DialogueOperationKind.ReviewSuggestions, turn.Text.Trim());
        if (command is DialogueOperationKind.PreferenceView or DialogueOperationKind.PreferenceSet or DialogueOperationKind.PreferenceReset)
            return new(context, command, turn.Text.Trim());
        if (command is null)
        {
            if (normalized is "настройки обучения" or "learning preferences") return new(context, DialogueOperationKind.PreferenceView, "");
            const string subjectPreference = "запомни для этой дисциплины ";
            const string globalPreference = "запомни для всех дисциплин ";
            if (normalized.StartsWith(subjectPreference, StringComparison.Ordinal))
                return new(context, DialogueOperationKind.PreferenceSet, "subject " + turn.Text.Trim()[subjectPreference.Length..]);
            if (normalized.StartsWith(globalPreference, StringComparison.Ordinal))
                return new(context, DialogueOperationKind.PreferenceSet, turn.Text.Trim()[globalPreference.Length..]);
            if (normalized is "сбрось настройки этой дисциплины") return new(context, DialogueOperationKind.PreferenceReset, "subject all");
            if (normalized is "сбрось общие учебные настройки") return new(context, DialogueOperationKind.PreferenceReset, "all");
        }
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
            if ((command ?? naturalAction) == DialogueOperationKind.EndSession)
                return await EndSessionAsync(context, turn, lesson, program, english, token);

            var sessionAction = command ?? naturalAction;
            if (program is not null && sessionAction is not (DialogueOperationKind.Progress or DialogueOperationKind.Finish or
                    DialogueOperationKind.Confirm or DialogueOperationKind.Cancel or DialogueOperationKind.None))
                returning = await sessions.RecordActivityAsync(turn, program.Id, lesson, token);
            if (returning && lesson is not null && (sessionAction != DialogueOperationKind.Resume ||
                lesson.Status == LessonStatus.AwaitingAnswer && context.PendingConfirmation is null && context.PendingClarification is null))
            {
                RestoreCurrentExercise(context, turn, lesson);
                return Recap(context, lesson) with { SuggestReviewsOnReturn = true };
            }
        }
        if (lesson is null && program is null && context.Consultation is { CreatedProgramId: null } &&
            command is DialogueOperationKind.Cancel or DialogueOperationKind.Pause or DialogueOperationKind.Resume)
            return await FirstGoalConsultation.DecideAsync(context, turn with
                { Command = null, Text = command == DialogueOperationKind.Resume ? "resume" : "pause" }, resolver, token);
        if (lesson is null && program is null && command is null && !anotherExercise &&
            (consultationEnabled || context.Consultation is not null))
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
        if (context.PendingConfirmation is not null && naturalAction is DialogueOperationKind.Pause or DialogueOperationKind.Resume)
            context.Synchronize(lesson, turn.Now);
        if (context.PendingConfirmation is { } confirmation && command is null)
            return ConfirmationReply(context, confirmation);

        if (context.PendingClarification is { } pendingSelection && (command is null or DialogueOperationKind.Answer) &&
            turn.ReceiptPromptId == pendingSelection.PromptId && turn.ReceiptLessonId == lesson?.Id &&
            LooksLikeChoiceAnswer(lesson, turn.Text) &&
            context.ConsumeClarification(DialogueOperationKind.Answer, turn.ReceivedAt, stamp, turn.Now, turn.SourceOrder) is not null)
            return await DecideActionAsync(context, turn, DialogueOperationKind.Answer, turn.Text, lesson, program, stamp, false, true, token);

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
            if (anotherExercise && program.PracticeStatus is LearningPracticeStatus.Running or LearningPracticeStatus.Paused)
                return PracticeAction(context, turn, DialogueOperationKind.NextPractice);
            if (command is null && (normalized is "хватит практики" or "закончим практику") &&
                (program.PracticeStatus is LearningPracticeStatus.Running or LearningPracticeStatus.Paused))
                return PracticeAction(context, turn, DialogueOperationKind.StopPractice);
            if (PracticeNavigation(program.PracticeStatus, command ?? naturalAction) is { } practiceAction)
                return PracticeAction(context, turn, practiceAction);
        }

        if (lesson is null && program?.HasEnglishDiagnostic == true &&
            (program.PendingDiagnosticQuestion is not null || command == DialogueOperationKind.Diagnose) &&
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
        if (anotherExercise)
            return lesson is not null ? Recap(context, lesson)
                : PracticeAction(context, turn, DialogueOperationKind.PracticeLastSkill);
        if (lesson is null && program is not null && (command ?? naturalAction) == DialogueOperationKind.Resume)
            return new(context, DialogueOperationKind.Next, "") { SuggestReviewsOnReturn = returning && program.PendingDiagnosticQuestion is null };

        var bound = lesson is not null && turn.ReceiptLessonId == lesson.Id &&
            (turn.ReceiptPromptId == context.PromptId || legacy && turn.ReceiptPromptId is null) &&
            (context.ResponseAfterSourceOrder == 0 || turn.SourceOrder > context.ResponseAfterSourceOrder);
        if (command is { } explicitAction)
            return await DecideActionAsync(context, turn, explicitAction, turn.Text, lesson, program, stamp, false, bound, token);
        if (normalized is "помощь" or "help" or "что дальше" or "как продолжить")
            return lesson is null ? new(context, null, "", new(DialogueReplyKind.NoActiveLesson, InEnglish: english)) : Recap(context, lesson);
        if (lesson is null && program is not null && FirstGoalConsultation.IsExplicitStudyGoal(normalized))
            return new(context, null, "", new(programSwitchEnabled ? DialogueReplyKind.AskNewGoal : DialogueReplyKind.NoActiveLesson, InEnglish: english));

        if (normalized is "другая тема" or "другая цель" or "another topic" or "another goal")
            return OpenChoice(context, turn, stamp, [DialogueOperationKind.Clarify, DialogueOperationKind.ChangeGoal]);
        var awaiting = lesson?.Status == LessonStatus.AwaitingAnswer;
        if (naturalAction is { } action && (!awaiting || lesson?.Choice is not null ||
            action is not (DialogueOperationKind.Clarify or DialogueOperationKind.RevealSolution)))
            return await DecideActionAsync(context, turn, action, turn.Text, lesson, program, stamp, false, bound, token);
        if (!awaiting && program is null)
            return new(context, null, "", new(lesson is null ? DialogueReplyKind.NoActiveLesson : DialogueReplyKind.Paused, InEnglish: english));
        if (awaiting && !bound || !awaiting && turn.SourceOrder <= context.ResponseAfterSourceOrder)
            return Reply(context, DialogueReplyKind.NeedBinding);
        if (awaiting && normalized is "давай" or "пропустить" or "пропустим" or "позже" or "skip" or "later")
            return Recap(context, lesson!);
        if (normalized is "ответ" or "вопрос" or "answer" or "question")
            return Reply(context, DialogueReplyKind.NeedActualInput);
        if (LooksLikeChoiceAnswer(lesson, turn.Text))
            return await DecideActionAsync(context, turn, DialogueOperationKind.Answer, turn.Text, lesson, program, stamp, false, bound, token);
        if (awaiting && normalized is "yes" or "no" && (lesson!.ResponseQuestion.Contains("yes or no", StringComparison.OrdinalIgnoreCase) ||
            lesson.ResponseQuestion.Contains("yes/no", StringComparison.OrdinalIgnoreCase)))
            return await DecideActionAsync(context, turn, DialogueOperationKind.Answer, turn.Text, lesson, program, stamp, false, bound, token);
        if (awaiting && (normalized is "да" or "нет" or "yes" or "no" or "потом" or "не знаю" ||
            normalized.Contains("ignore previous", StringComparison.Ordinal) || normalized.Contains("ignore system", StringComparison.Ordinal)))
            return OpenChoice(context, turn, stamp, [DialogueOperationKind.Answer, DialogueOperationKind.Clarify]);
        LearningIntent[] allowed = awaiting
            ? [LearningIntent.Ambiguous, LearningIntent.Answer, LearningIntent.Clarify, LearningIntent.Pause, LearningIntent.Resume,
                LearningIntent.Progress, LearningIntent.Next, LearningIntent.GoalChange, LearningIntent.EndSession,
                LearningIntent.RevealSolution, LearningIntent.PreviousTopic, LearningIntent.ExplainAssessment, LearningIntent.ShowCriteria]
            : [LearningIntent.Ambiguous, LearningIntent.Resume, LearningIntent.Next, LearningIntent.Progress,
                LearningIntent.EndSession, LearningIntent.PreviousTopic, LearningIntent.ExplainAssessment, LearningIntent.ShowCriteria];
        LearningIntentProposal proposal;
        try
        {
            proposal = await resolver.ResolveAsync(new(turn.Text, lesson?.ResponseQuestion ?? "", program?.Goal, program?.Discipline,
                lesson?.Interactions.OrderByDescending(interaction => interaction.Id).Take(3)
                    .Select(interaction => interaction.Explanation[..Math.Min(500, interaction.Explanation.Length)]).ToArray() ?? [], allowed,
                program?.Id, program?.RouteVersion, program?.Version, context.Revision, context.PromptId), token);
        }
        catch (Learning.LessonProviderException)
        {
            proposal = new(LearningIntent.Ambiguous);
        }
        if (!allowed.Contains(proposal.Intent) || proposal.Intent == LearningIntent.Ambiguous)
        {
            if (!awaiting) return new(context, null, "", new(lesson is null ? DialogueReplyKind.NoActiveLesson : DialogueReplyKind.Paused, InEnglish: english));
            var conflictingNavigation = (normalized.Contains("пауза", StringComparison.Ordinal) || normalized.Contains("pause", StringComparison.Ordinal)) &&
                (normalized.Contains("продолж", StringComparison.Ordinal) || normalized.Contains("resume", StringComparison.Ordinal));
            return OpenChoice(context, turn, stamp, conflictingNavigation
                ? [DialogueOperationKind.Pause, DialogueOperationKind.Resume] : [DialogueOperationKind.Answer, DialogueOperationKind.Clarify]);
        }
        if (awaiting && proposal.Intent == LearningIntent.ShowCriteria)
        {
            var requirements = lesson!.AssessmentContent?.StructuredRubric?.QuestionText() ?? lesson.Question;
            return new(context, null, "", new(DialogueReplyKind.ExerciseCriteria, [requirements], english));
        }
        if (proposal.Intent == LearningIntent.ExplainAssessment && lesson is
            { Status: LessonStatus.AwaitingAnswer, HasPendingLegacyAssessment: false, RubricEvaluation.Final: false })
            return new(context, null, "", new(DialogueReplyKind.ExerciseCriteria, [lesson.ResponseQuestion], english));
        if (proposal.Intent == LearningIntent.EndSession && sessions is not null && turn.SourceOrder > 0)
            return await EndSessionAsync(context, turn, lesson, program, english, token);
        var proposedAction = proposal.Intent switch
        {
            LearningIntent.Answer => DialogueOperationKind.Answer,
            LearningIntent.Clarify => DialogueOperationKind.Clarify,
            LearningIntent.Pause => DialogueOperationKind.Pause,
            LearningIntent.Resume => DialogueOperationKind.Resume,
            LearningIntent.Progress => DialogueOperationKind.Progress,
            LearningIntent.Next => DialogueOperationKind.Next,
            LearningIntent.EndSession => DialogueOperationKind.Pause,
            LearningIntent.PreviousTopic => DialogueOperationKind.PreviousTopic,
            LearningIntent.RevealSolution => DialogueOperationKind.RevealSolution,
            LearningIntent.ExplainAssessment or LearningIntent.ShowCriteria => DialogueOperationKind.ExplainAssessment,
            _ => DialogueOperationKind.ChangeGoal
        };
        if (proposedAction == DialogueOperationKind.ExplainAssessment)
            return new(context, proposedAction, "");
        return await DecideActionAsync(context, turn, proposedAction, turn.Text, lesson, program, stamp, false, bound, token);
    }

    private async Task<DialogueDecision> EndSessionAsync(DialogueContext context, DialogueTurn turn, Lesson? lesson,
        DialogueProgramSnapshot? program, bool english, CancellationToken token)
    {
        if (turn.SourceOrder <= context.ResponseAfterSourceOrder)
            return new(context, null, "", new(DialogueReplyKind.NeedBinding, InEnglish: english));
        await sessions!.EndAsync(turn, program?.Id, lesson, token);
        if (lesson is not null) context.Synchronize(lesson, turn.Now, turn.SourceOrder);
        context.AdvanceResponseBoundary(turn.SourceOrder);
        return new(context, null, "", new(DialogueReplyKind.SessionEnded, InEnglish: english));
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
        if (bound && CurriculumService.WantsFoundations(turn.Text))
            return new(context, DialogueOperationKind.CompleteDiagnostic, turn.Text);

        if (action == DialogueOperationKind.Resume ||
            (normalized is "давай" or "start" or "начнём диагностику" || !answering && normalized is "да" or "yes") && (bound || !offer))
        {
            if (!answering) context.BindDiagnosticQuestion(program.Id, question, turn.Now, turn.SourceOrder);
            return new(context, null, "", new(DialogueReplyKind.DiagnosticQuestion, [question], english));
        }
        if (!answering) return new(context, null, "", new(DialogueReplyKind.DiagnosticOffer, InEnglish: english));
        if (bound && !skip && action is null && normalized is not ("помощь" or "help") &&
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
        if (PracticeNavigation(program?.PracticeStatus, action) is { } practiceAction)
            return PracticeAction(context, turn, practiceAction);
        if (lesson is null && program is not null && action == DialogueOperationKind.Resume)
            action = DialogueOperationKind.Next;
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
    private static DialogueOperationKind? PracticeNavigation(LearningPracticeStatus? status, DialogueOperationKind? action) =>
        (status, action) switch
        {
            (not null, DialogueOperationKind.Next) => DialogueOperationKind.NextPractice,
            (LearningPracticeStatus.Running or LearningPracticeStatus.Paused, DialogueOperationKind.Pause) => DialogueOperationKind.PausePractice,
            (LearningPracticeStatus.Running or LearningPracticeStatus.Paused, DialogueOperationKind.Resume) => DialogueOperationKind.ResumePractice,
            _ => null
        };

    private static DialogueDecision Recap(DialogueContext context, Lesson lesson) => new(context, null, "",
        new(DialogueReplyKind.Recap, InEnglish: FirstGoalConsultation.InEnglish(lesson.Theory), Recap: LearningService.Session(lesson)));
    private static bool LooksLikeChoiceAnswer(Lesson? lesson, string text) =>
        lesson is { Status: LessonStatus.AwaitingAnswer, Choice: { } choice } && ChoiceAnswerText.LooksLikeAnswer(choice.Exercise, text);
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
        if (text is "завершить сессию" or "закончить сессию" or "на сегодня хватит" or "end session" or "that's enough for today") return DialogueOperationKind.EndSession;
        if (text is "пауза" or "поставь на паузу" or "мне пора, продолжим позже" or "продолжим позже" or "pause" or "stop") return DialogueOperationKind.Pause;
        if (text is "продолжим" or "продолжить" or "давай продолжим" or "resume" or "continue") return DialogueOperationKind.Resume;
        if (text is "прогресс" or "мой прогресс" or "покажи прогресс" or "progress") return DialogueOperationKind.Progress;
        if (text is "дальше" or "следующее упражнение" or "next") return DialogueOperationKind.Next;
        if (text is "завершить упражнение" or "закончу занятие" or "finish") return DialogueOperationKind.Finish;
        if (text is "покажи решение" or "покажи полное решение" or "дай полный ответ" or "show the solution")
            return DialogueOperationKind.RevealSolution;
        if (text is "дай подсказку" or "подскажи" or "подсказка" or "hint") return DialogueOperationKind.Clarify;
        if (text.StartsWith("хочу другую цель", StringComparison.Ordinal) || text.StartsWith("сменить цель", StringComparison.Ordinal)) return DialogueOperationKind.ChangeGoal;
        return null;
    }
}
