using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class TelegramKnowledgePresenterTests
{
    [Fact]
    public void Status_MaximumEvidence_KeepsFactsAndHypothesesInReadableTelegramMessages()
    {
        var skill = Skill.Create(new UserId(1), new string('t', 500));
        var lesson = Lesson.Start(skill.UserId, skill.Title, "Theory", new string('q', 1000));
        lesson.LinkToSkill(skill);
        lesson.Complete(new string('a', 3000), new string('f', 3000), false);
        var evidence = LearningEvidence.FromAnswer(lesson, DateTimeOffset.UnixEpoch, new string('s', 200));
        var status = new SkillStatus(skill, LearnerSkillState.Evaluate([evidence]), [evidence]);
        var messages = TelegramKnowledgePresenter.Status(status);
        Assert.Equal(3, messages.Count);
        Assert.All(messages.Concat(TelegramKnowledgePresenter.List([skill])), m => Assert.InRange(m.Length, 1, 4096));
        Assert.Contains("provisional", messages[0]);
        Assert.Contains("Observed answer", messages[1]);
        Assert.Contains("AI interpretation", messages[2]);
        Assert.Contains(evidence.AssessmentFeedback, messages[2]);
    }
}
