using OwnDay.Application.Learning;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class TelegramLearningPresenterTests
{
    [Fact]
    public void Render_MaximumMaterialAndExplanation_SplitsReadableMessagesWithinTelegramLimit()
    {
        var topic = new string('t', 500);
        var question = new string('q', 1000);
        var theory = new string('a', 2500);
        var material = TelegramLearningPresenter.Render(new LearningResult.Material(theory, question, topic));
        var explanation = TelegramLearningPresenter.Render(new LearningResult.Explanation(theory, topic, question));
        Assert.Equal(2, material.Count);
        Assert.Equal(2, explanation.Count);
        Assert.All(material.Concat(explanation), text => Assert.InRange(text.Length, 1, 4096));
        Assert.Contains(topic, material[0]);
        Assert.Contains("/answer", material[1]);
        Assert.Contains(question, explanation[1]);
    }
}
