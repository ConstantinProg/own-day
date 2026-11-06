using System.Globalization;
using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramKnowledgeCommandHandler(KnowledgeService knowledge, LearningService learning)
{
    public bool Handles(string name) => name is "learn_skills" or "learn_status" or "learn_practice" or
        "learn_reviews" or "learn_review" or "learn_review_later" or "learn_review_skip";

    public async Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        if (command.Name is "learn_skills" or "learn_reviews")
        {
            var page = 1;
            if (!string.IsNullOrWhiteSpace(command.Arguments) &&
                (!int.TryParse(command.Arguments.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out page) ||
                 page is < 1 or > KnowledgeService.MaxPageNumber))
                return [$"Use /{command.Name} [page] with a positive page number."];
            if (command.Name == "learn_reviews")
                return TelegramKnowledgePresenter.Reviews(await knowledge.ReviewsAsync(command.UserId, token, page));
            return TelegramKnowledgePresenter.List(await knowledge.ListAsync(command.UserId, token, page));
        }
        if (!Guid.TryParse(command.Arguments.Trim(), out var id) || id == Guid.Empty)
            return [$"Use /{command.Name} <skill id>. /learn_skills or /learn_reviews"];
        if (command.Name == "learn_review")
            return TelegramLearningPresenter.Render(await learning.ReviewAsync(command.UserId, id, token));
        if (command.Name is "learn_review_later" or "learn_review_skip")
            return TelegramLearningPresenter.Render(await learning.DeferReviewAsync(command.UserId, id,
                command.Name == "learn_review_skip" ? ReviewDeferral.Skip : ReviewDeferral.Postpone, token));
        if (command.Name == "learn_practice")
            return TelegramLearningPresenter.Render(await learning.PracticeAsync(command.UserId, id, token));
        var status = await knowledge.StatusAsync(command.UserId, id, token);
        return status is null ? ["Skill not found. /learn_skills"] : TelegramKnowledgePresenter.Status(status);
    }
}
