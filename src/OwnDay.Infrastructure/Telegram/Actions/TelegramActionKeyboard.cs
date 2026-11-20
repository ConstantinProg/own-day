using System.Text.Json;
using OwnDay.Application.Learning;
using OwnDay.Application.TesterMode;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Commands;
using Telegram.Bot.Types.ReplyMarkups;

namespace OwnDay.Infrastructure.Telegram.Actions;

public sealed class TelegramActionKeyboard(TelegramActionCodec codec)
{
    internal TelegramLearningReply RenderConfirmation(string text, string label, TesterModeConfirmation confirmation, long owner, long chat)
    {
        var button = InlineKeyboardButton.WithCallbackData(label, codec.Encode(confirmation, owner, chat));
        return new(text, JsonSerializer.Serialize(new InlineKeyboardMarkup(new[] { new[] { button } })));
    }

    internal TelegramLearningReply Render(TelegramLearningReply reply, long owner, long chat, DateTimeOffset now,
        DialogueContext? dialogue, Lesson? lesson = null)
    {
        if (reply.Actions is not { Count: > 0 } || owner != chat) return reply;
        var rows = new List<InlineKeyboardButton[]>();
        var russian = reply.InRussian ?? MarketLearningContent.InRussian(reply.Text);
        foreach (var action in reply.Actions)
        {
            if (!LearningActionProvider.CanOffer(action.Kind, lesson)) continue;
            var (bound, expiresAt) = Bind(action, dialogue, now);
            if (bound is null || expiresAt <= now) continue;
            if (action.Kind is LearningActionKind.Hint or LearningActionKind.Solution or LearningActionKind.Criteria)
                bound = bound with { TargetId = lesson!.Id };
            var label = Label(bound, reply.Choice, russian);
            rows.Add([InlineKeyboardButton.WithCallbackData(label, codec.Encode(bound, owner, chat, expiresAt))]);
        }
        return rows.Count == 0 ? reply : reply with { ReplyMarkupJson = JsonSerializer.Serialize(new InlineKeyboardMarkup(rows)) };
    }

    private static (LearningAction? Action, DateTimeOffset ExpiresAt) Bind(LearningAction action, DialogueContext? dialogue, DateTimeOffset now)
    {
        if (action.Kind is LearningActionKind.Confirm or LearningActionKind.Reject)
            return dialogue?.PendingConfirmation is { } proposal
                ? (action with { TargetId = proposal.ProposalId, Revision = proposal.BaseRevision }, proposal.ExpiresAt) : default;
        if (action.Kind is >= LearningActionKind.ClarifyAnswer and <= LearningActionKind.ClarifyContinue)
            return dialogue?.PendingClarification is { } clarification
                ? (action with { TargetId = clarification.PromptId, Revision = clarification.BaseRevision }, clarification.ExpiresAt) : default;
        if (action.Kind <= LearningActionKind.SubmitChoice ||
            action.Kind is LearningActionKind.StartInvitation or LearningActionKind.PostponeInvitation or LearningActionKind.SkipInvitation)
            return (action, now.AddDays(1));
        return (action with { Revision = action.Revision ^ (dialogue?.Revision ?? 0) }, now.AddDays(1));
    }

    private static string Label(LearningAction action, ChoicePrompt? prompt, bool russian)
    {
        if (prompt is not null && action.Kind is LearningActionKind.SelectChoice or LearningActionKind.ToggleChoice)
        {
            var index = 0;
            while (prompt.Options[index].Id != action.Option) index++;
            var labels = russian ? ChoicePrompt.CyrillicLabels : ChoicePrompt.LatinLabels;
            var label = $"{labels[index]}. {LearningService.ChoiceLabel(prompt.Options[index], prompt.Format, russian)}";
            if (action.Kind == LearningActionKind.ToggleChoice)
                label += prompt.Draft.Contains(action.Option)
                    ? russian ? " — выбрано" : " — selected"
                    : russian ? " — не выбрано" : " — not selected";
            return label;
        }
        if (action.Kind is LearningActionKind.ProjectReportPage or LearningActionKind.ProgramsPage or LearningActionKind.ReviewsPage)
        {
            var page = action.Number;
            return russian ? $"Страница {page}" : $"Page {page}";
        }
        if (action.Kind == LearningActionKind.RouteVersion)
        {
            var version = action.Number;
            return russian ? $"Версия {version}" : $"Version {version}";
        }
        var (ru, en) = action.Kind switch
        {
            LearningActionKind.SubmitChoice => ("Ответить", "Submit"),
            LearningActionKind.Continue or LearningActionKind.ClarifyContinue => ("Продолжить", "Continue"),
            LearningActionKind.Next => ("Дальше", "Next"),
            LearningActionKind.Hint or LearningActionKind.DiagnosisHelp => ("Подсказка", "Hint"),
            LearningActionKind.Solution => ("Показать решение", "Show solution"),
            LearningActionKind.Criteria => ("Критерии", "Criteria"),
            LearningActionKind.ExplainAssessment => ("Разобрать оценку", "Explain assessment"),
            LearningActionKind.AnotherExercise => ("Ещё задание", "Another exercise"),
            LearningActionKind.ReviewSuggestions => ("Что повторить", "What to review"),
            LearningActionKind.Confirm => ("Подтвердить", "Confirm"),
            LearningActionKind.Reject => ("Отклонить", "Decline"),
            LearningActionKind.ClarifyAnswer => ("Это ответ", "It's an answer"),
            LearningActionKind.ClarifyQuestion => ("Это вопрос", "It's a question"),
            LearningActionKind.ClarifyGoal => ("Сменить цель", "Change goal"),
            LearningActionKind.ClarifyPause or LearningActionKind.PausePractice or LearningActionKind.PauseDiagnosis => ("Пауза", "Pause"),
            LearningActionKind.StopPractice => ("Завершить практику", "End practice"),
            LearningActionKind.RetryConsultation => ("Повторить", "Retry"),
            LearningActionKind.NoPriorExperience => ("С нуля", "No prior experience"),
            LearningActionKind.StartDiagnosis => ("Начать диагностику", "Start diagnostic"),
            LearningActionKind.SkipDiagnosis => ("Пропустить диагностику", "Skip diagnostic"),
            LearningActionKind.StartInvitation => ("Начать", "Start"),
            LearningActionKind.PostponeInvitation or LearningActionKind.PostponeReview => ("Позже", "Later"),
            LearningActionKind.SkipInvitation or LearningActionKind.SkipReview => ("Пропустить", "Skip"),
            LearningActionKind.PauseReminders => ("Приостановить напоминания", "Pause reminders"),
            LearningActionKind.ResumeReminders => ("Возобновить напоминания", "Resume reminders"),
            LearningActionKind.StopReminders => ("Отключить напоминания", "Turn off reminders"),
            LearningActionKind.SelectProgram => ("Выбрать программу", "Select program"),
            LearningActionKind.RouteTree => ("Дерево тем", "Topic tree"),
            LearningActionKind.RouteHistory => ("История маршрута", "Route history"),
            LearningActionKind.Progress => ("Прогресс", "Progress"),
            LearningActionKind.StartReview => ("Повторить", "Review"),
            LearningActionKind.SkillPractice => ("Практика", "Practice"),
            LearningActionKind.ProjectPractice => ("Практика по задаче", "Practice this task"),
            LearningActionKind.FocusTopic => ("Проверить тему", "Practise this topic"),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return russian ? ru : en;
    }
}
