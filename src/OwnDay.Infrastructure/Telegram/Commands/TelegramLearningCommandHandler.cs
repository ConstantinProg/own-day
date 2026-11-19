using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

// Shared command parsing and presentation for synchronous compatibility and the
// background runner. All state transitions remain in Application/Domain use cases.
public sealed class TelegramLearningCommandHandler(LearningService learning,
    TelegramCurriculumCommandHandler curriculum, TelegramKnowledgeCommandHandler knowledge, TelegramStudyCommandHandler study)
{
    public bool Handles(string name) => study.Handles(name) || curriculum.Handles(name) || knowledge.Handles(name) ||
        name is "learn" or "answer" or "learn_ask" or "learn_explain" or "learn_pause" or "learn_resume" or "learn_finish" or
            "learn_confirm" or "learn_end_session" or "learn_solution" or "learn_assessment" or "learn_appeal" or "learn_route_edit" or
            "learn_goal_update" or "learn_recommend_reviews";

    public Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, CancellationToken token) => command.Name switch
    {
        _ when study.Handles(command.Name) => study.HandleAsync(command, token),
        _ when curriculum.Handles(command.Name) => curriculum.HandleAsync(command, token),
        _ when knowledge.Handles(command.Name) => knowledge.HandleAsync(command, token),
        "learn_end_session" => Task.FromResult<IReadOnlyList<string>>(["Напишите «завершить сессию»."]),
        "learn_assessment" or "learn_appeal" => Task.FromResult<IReadOnlyList<string>>(["Напишите «почему такая оценка»."]),
        "learn_route_edit" => Task.FromResult<IReadOnlyList<string>>(TelegramDialoguePresenter.Render(new(OwnDay.Application.Dialogue.DialogueReplyKind.RouteEditHelp))),
        "learn_goal_update" => Task.FromResult<IReadOnlyList<string>>(TelegramCurriculumPresenter.Render(new CurriculumResult.GoalEditRejected())),
        "learn_recommend_reviews" => Task.FromResult<IReadOnlyList<string>>(["Напишите «что повторить» или воспользуйтесь /learn_reviews для списка по срокам."]),
        _ => HandleLessonAsync(command, token)
    };

    private async Task<IReadOnlyList<string>> HandleLessonAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        if (command.Name == "learn_confirm") return ["Нет актуального подтверждения. Повторите исходный запрос."];
        var result = command.Name switch
        {
            "learn" => await learning.StartAsync(command.UserId, command.Arguments, token),
            "answer" => await learning.AnswerAsync(command.UserId, command.Arguments, token),
            "learn_ask" => await learning.ClarifyAsync(command.UserId, command.Arguments, token),
            "learn_explain" => await learning.ExplainAsync(command.UserId, token),
            "learn_solution" => await learning.RevealAsync(command.UserId, token),
            "learn_pause" => await learning.PauseAsync(command.UserId, token),
            "learn_resume" => await learning.ResumeAsync(command.UserId, token),
            "learn_finish" => await learning.FinishAsync(command.UserId, token),
            _ => throw new InvalidOperationException("Unsupported Learning command.")
        };
        return TelegramLearningPresenter.Render(result);
    }
}
