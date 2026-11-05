using OwnDay.Application.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

public static class TelegramLearningPresenter
{
    private const string Actions = "/answer <answer>\n/learn_ask <question> · /learn_explain\n/learn_resume · /learn_pause · /learn_finish";

    public static IReadOnlyList<string> Render(LearningResult result) => result switch
    {
        LearningResult.Material material => [material.Topic + "\n\n" + material.Theory, material.Question + "\n\n" + Actions],
        LearningResult.Assessment assessment => ["AI assessment (may be inaccurate):\n" + assessment.Feedback + "\n\n/learn <topic>"],
        LearningResult.InvalidTopic => ["Use /learn <topic> (up to 500 characters)."],
        LearningResult.InvalidAnswer => ["Use /answer <answer> (up to 3000 characters)."],
        LearningResult.NoActiveLesson => ["Start a lesson with /learn <topic>."],
        LearningResult.ProviderUnavailable => ["The teacher is temporarily unavailable or returned invalid material. Retry the command."],
        LearningResult.Session session => [session.ContextSummary + (session.Status == OwnDay.Domain.Learning.LessonStatus.Paused
            ? "\n\nLesson paused. /learn_resume or /learn_finish"
            : "\n\n" + Actions)],
        LearningResult.Explanation explanation => [explanation.Text, "Return to: " + explanation.Topic + "\n" + explanation.Question + "\n\n" + Actions],
        LearningResult.InvalidClarification => ["Use /learn_ask <question> (up to 1000 characters)."],
        LearningResult.InvalidTransition => ["This action is unavailable in the current lesson state. Use /learn_resume or /learn_finish."],
        LearningResult.Finished => ["Lesson finished without an assessment. /learn <topic>"],
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };
}
