using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramLearningPresenter
{
    public static string Render(LearningResult result) => result switch
    {
        LearningResult.Material material => material.Theory + "\n\n" + material.Question + "\n\n/answer <answer>",
        LearningResult.Assessment assessment => "AI assessment (may be inaccurate):\n" + assessment.Feedback + "\n\n/learn <topic>",
        LearningResult.InvalidTopic => "Use /learn <topic> (up to 500 characters).",
        LearningResult.InvalidAnswer => "Use /answer <answer> (up to 3000 characters).",
        LearningResult.NoActiveLesson => "Start a lesson with /learn <topic>.",
        LearningResult.ProviderUnavailable => "The teacher is temporarily unavailable or returned invalid material. Retry the command.",
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };
}
