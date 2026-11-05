using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class TelegramLearningPresenterTests
{
    [Fact]
    public void Render_ActiveSession_ShowsCurrentQuestionAndAnswerAction()
    {
        var reply = Assert.Single(TelegramLearningPresenter.Render(
            new LearningResult.Session("Algebra", "Two plus two?", LessonStatus.AwaitingAnswer)));

        Assert.Contains("Topic: Algebra", reply);
        Assert.Contains("Current question: Two plus two?", reply);
        Assert.Contains("Expected action: Answer the current question.", reply);
        Assert.Contains("/answer <answer>", reply);
        Assert.DoesNotContain("Lesson paused", reply);
    }

    [Fact]
    public void Render_PausedSession_OffersResumeWithoutAnswerAction()
    {
        var reply = Assert.Single(TelegramLearningPresenter.Render(
            new LearningResult.Session("Algebra", "Two plus two?", LessonStatus.Paused)));

        Assert.Contains("Topic: Algebra", reply);
        Assert.Contains("Current question: Two plus two?", reply);
        Assert.Contains("Expected action: Resume the lesson before answering.", reply);
        Assert.Contains("Lesson paused. /learn_resume or /learn_finish", reply);
        Assert.DoesNotContain("/answer", reply);
    }

    [Fact]
    public void Render_MaximumMaterialAndExplanation_SplitsReadableMessagesWithinTelegramLimit()
    {
        var topic = new string('t', 500);
        var question = new string('q', 1000);
        var theory = new string('a', 2500);
        var material = TelegramLearningPresenter.Render(new LearningResult.Material(theory, question, topic));
        var explanation = TelegramLearningPresenter.Render(new LearningResult.Explanation(theory, topic, question));
        var session = TelegramLearningPresenter.Render(new LearningResult.Session(topic, question, LessonStatus.AwaitingAnswer));
        var paused = TelegramLearningPresenter.Render(new LearningResult.Session(topic, question, LessonStatus.Paused));
        Assert.Equal(2, material.Count);
        Assert.Equal(2, explanation.Count);
        Assert.All(material.Concat(explanation).Concat(session).Concat(paused), text => Assert.InRange(text.Length, 1, 4096));
        Assert.Contains(topic, material[0]);
        Assert.Contains("/answer", material[1]);
        Assert.Contains(question, explanation[1]);
    }
}
