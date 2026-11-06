using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class TelegramCurriculumPresenterTests
{
    [Fact]
    public void Render_ProjectReportWithMaximumText_LabelsProposalsAndFitsTelegramMessages()
    {
        var user = new UserId(1);
        var program = LearningProgram.Create(user, new string('g', 500), "Beginner", 1, new string('d', 100),
            Enumerable.Range(0, 12).Select(i => new CurriculumTopic(i.ToString().PadLeft(40, 'c'), new string('t', 100), [])), null);
        var project = OwnDay.Domain.Projects.Project.Create(user, new string('p', 200), DateTime.UnixEpoch);
        var report = new ProjectLearningReport(program, project,
            program.CurrentRoute.Topics.Select(t => new ProjectTopicRecommendation(t, null, true)).ToArray(),
            Enumerable.Range(1, 10).Select(i => new ProjectActionOption(i, new string('a', 200))).ToArray(), 1, true);
        var messages = TelegramCurriculumPresenter.Render(new CurriculumResult.ProjectReport(report));
        Assert.All(messages, m => Assert.InRange(m.Length, 1, 4096));
        var text = string.Join('\n', messages);
        Assert.Contains("AI-proposed skills", text);
        Assert.Contains("No Commitments are created", text);
        Assert.Contains("/learn_project_report 2", text);
        Assert.Contains("/learn_project_practice", text);
    }

    [Fact]
    public void Render_AdaptedLesson_ExplainsAdjustmentAndKeepsEveryMessageWithinTelegramLimit()
    {
        var result = new CurriculumResult.Adapted(new LearningResult.Material(new string('T', 2500), new string('Q', 1000),
            new string('S', 100)), new string('R', 500));
        var messages = TelegramCurriculumPresenter.Render(result);
        Assert.Contains("Teaching adjustment", messages[0]);
        Assert.Contains("/learn_route", messages[0]);
        Assert.Contains("/answer", string.Join('\n', messages));
        Assert.All(messages, m => Assert.InRange(m.Length, 1, 4096));
    }

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
