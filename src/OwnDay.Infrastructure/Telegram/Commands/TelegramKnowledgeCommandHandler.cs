using System.Globalization;
using OwnDay.Application.Interactions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramKnowledgeCommandHandler(KnowledgeService knowledge, LearningService learning)
{
    public bool Handles(string name) => name is "learn_skills" or "learn_status" or "learn_practice" or
        "learn_reviews" or "learn_review" or "learn_review_later" or "learn_review_skip" or
        "learn_preferences" or "learn_preference" or "learn_preference_reset";

    public async Task<IReadOnlyList<string>> HandleAsync(ProcessIncomingCommand command, CancellationToken token)
    {
        if (command.Name is "learn_preferences" or "learn_preference" or "learn_preference_reset")
        {
            if (command.Name == "learn_preferences")
            {
                if (!string.IsNullOrWhiteSpace(command.Arguments)) return TelegramPersonalizationPresenter.Usage;
            }
            else if (command.Name == "learn_preference")
            {
                var parts = command.Arguments.Split('|', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !TryPreference(parts[0], out var kind) ||
                    !await knowledge.SetPreferenceAsync(command.UserId, kind, parts[1], token))
                    return TelegramPersonalizationPresenter.Usage;
            }
            else
            {
                var argument = command.Arguments.Trim();
                LearningPreferenceKind? kind = null;
                if (argument != "all")
                {
                    if (!TryPreference(argument, out var parsed)) return TelegramPersonalizationPresenter.Usage;
                    kind = parsed;
                }
                await knowledge.ResetPreferenceAsync(command.UserId, kind, token);
            }
            return TelegramPersonalizationPresenter.Render(await knowledge.PersonalizationAsync(command.UserId, token));
        }
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

    private static bool TryPreference(string value, out LearningPreferenceKind kind) =>
        Enum.TryParse(value, true, out kind) && Enum.IsDefined(kind) &&
        kind.ToString().Equals(value, StringComparison.OrdinalIgnoreCase);
}
