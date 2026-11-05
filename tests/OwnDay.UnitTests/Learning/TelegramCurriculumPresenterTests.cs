using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class TelegramCurriculumPresenterTests
{
    [Fact]
    public void Render_MaximumRouteWithDenseDependencies_KeepsEveryMessageWithinTelegramLimit()
    {
        var topics = Enumerable.Range(0, 12).Select(i =>
            new CurriculumTopic(i.ToString().PadLeft(40, 't'), new string('T', 100),
                Enumerable.Range(0, i).Select(p => p.ToString().PadLeft(40, 't')).ToArray())).ToArray();
        var program = LearningProgram.Create(new UserId(1), new string('G', 500), new string('E', 1000), null,
            new string('D', 100), topics, new string('Q', 1000));

        var messages = TelegramCurriculumPresenter.Render(new CurriculumResult.Program(program));

        Assert.All(messages, m => Assert.InRange(m.Length, 1, 4096));
        Assert.Contains("/learn_diagnose skip", messages[^1]);
        Assert.Contains("requires:", string.Join('\n', messages));
        Assert.All(topics, t => Assert.Contains(t.Code + ":", string.Join('\n', messages)));
    }
}
