using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramKnowledgeCommandHandler(KnowledgeService knowledge, LearningService learning)
{
    public bool Handles(string name) => name is "learn_skills" or "learn_status" or "learn_practice";

    public async Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        if (command.Name == "learn_skills" && string.IsNullOrWhiteSpace(command.Arguments))
            return TelegramKnowledgePresenter.List(await knowledge.ListAsync(command.UserId, token));
        if (command.Name == "learn_skills" || !Guid.TryParse(command.Arguments.Trim(), out var id) || id == Guid.Empty)
            return ["Use /learn_skills, /learn_status <skill id>, or /learn_practice <skill id>."];
        if (command.Name == "learn_practice")
            return TelegramLearningPresenter.Render(await learning.PracticeAsync(command.UserId, id, token));
        var status = await knowledge.StatusAsync(command.UserId, id, token);
        return status is null ? ["Skill not found. /learn_skills"] : TelegramKnowledgePresenter.Status(status);
    }
}
