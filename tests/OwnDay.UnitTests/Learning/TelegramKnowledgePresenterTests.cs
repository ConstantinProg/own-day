using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class TelegramKnowledgePresenterTests
{
    [Fact]
    public void Reviews_MaximumTitles_SplitsMessagesAndPreservesAllCommandsAndPageNavigation()
    {
        var items = Enumerable.Range(0, KnowledgeService.PageSize).Select(i =>
        {
            var skill = Skill.Create(new UserId(1), i + new string('t', 498));
            return new ReviewItem(skill, ReviewSchedule.FromAnswer(LearnerSkillStateTests.Answer(skill, true)));
        }).ToArray();
        var messages = TelegramKnowledgePresenter.Reviews(new(items, 2, true));
        Assert.All(messages, m => Assert.InRange(m.Length, 1, 4096));
        var text = string.Join("\n", messages);
        Assert.All(items, item => Assert.Contains($"/learn_review {item.Skill.Id}", text));
        Assert.Contains("Previous: /learn_reviews 1", text);
        Assert.Contains("Next: /learn_reviews 3", text);
    }

    [Fact]
    public void List_MaximumTitles_CombinesSkillsWithinTelegramLimitAndShowsNavigation()
    {
        var skills = Enumerable.Range(0, KnowledgeService.PageSize)
            .Select(i => Skill.Create(new UserId(1), i + new string('t', 498))).ToArray();
        var messages = TelegramKnowledgePresenter.List(new SkillPage(skills, 2, true));
        Assert.InRange(messages.Count, 1, 2);
        Assert.All(messages, m => Assert.InRange(m.Length, 1, 4096));
        var text = string.Join("\n", messages);
        Assert.All(skills, skill => Assert.Contains(skill.Title, text));
        Assert.Contains("Previous: /learn_skills 1", text);
        Assert.Contains("Next: /learn_skills 3", text);
    }

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
        Assert.All(messages.Concat(TelegramKnowledgePresenter.List(new SkillPage([skill], 1, false))), m => Assert.InRange(m.Length, 1, 4096));
        Assert.Contains("provisional", messages[0]);
        Assert.Contains("Observed answer", messages[1]);
        Assert.Contains("AI interpretation", messages[2]);
        Assert.Contains(evidence.AssessmentFeedback, messages[2]);
    }
}
