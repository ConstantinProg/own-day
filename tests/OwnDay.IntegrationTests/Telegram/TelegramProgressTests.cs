using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramProgressTests
{
    [Fact]
    public async Task Post_ProgressPages_ExplainsEvidenceAndKeepsSelectionAndOwnersIsolated()
    {
        using var host = new OwnDayHostFactory();
        using var client = host.CreateClient();
        var user = new UserId(101);
        var project = Project.Create(user, "Owned project", DateTime.UtcNow);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            db.Add(project);
            await db.SaveChangesAsync();
        }
        var programs = Enumerable.Range(0, 3).Select(i => LearningProgram.Create(user, $"Goal {i}", "Beginner",
            i == 0 ? project.Id : null, "Mathematics", [new("basics", "Basics", []), new("advanced", "Advanced", ["basics"])], null)).ToArray();
        var skill = Skill.Create(user, "Shared algebra");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            db.AddRange(programs);
            db.Add(LearningContext.Create(programs[0]));
            db.Add(LearningProgram.Create(new(202), "Private foreign program", "Beginner", null, "Secret", [new("private", "Private", [])], null));
            db.Add(skill);
            db.Add(ProgramSkillLink.Confirm(programs[0], "basics", skill, "Confirmed algebra identity", DateTimeOffset.UtcNow));
            foreach (var correct in new[] { false, true })
            {
                var lesson = Lesson.Start(user, "Basics", "Theory", "Question");
                lesson.LinkToProgram(programs[0], "basics");
                lesson.LinkToSkill(skill);
                lesson.Complete("answer", "Reason for this assessment", correct);
                var evidence = LearningEvidence.FromAnswer(lesson, DateTimeOffset.UtcNow.AddDays(-2), "test-model");
                db.Add(lesson);
                db.Add(evidence);
                if (correct) db.Add(ReviewSchedule.FromAnswer(evidence));
            }
            await db.SaveChangesAsync();
        }
        for (var page = 1; page <= 4; page++)
            await TelegramCurriculumTests.PostAsync(client, page, 101, $"/learn_progress {page}");
        await TelegramCurriculumTests.PostAsync(client, 1, 101, "/learn_progress 1");
        await TelegramCurriculumTests.PostAsync(client, 5, 303, "/learn_progress");
        using var inspection = host.Services.CreateScope();
        var context = inspection.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var replies = await context.TelegramOutboxMessages.ToListAsync();
        var text = string.Join('\n', replies.Where(r => r.ChatId == 101).Select(r => r.Text));
        Assert.Contains("Skill estimate: Conflicting; answers: 2; credited positive: 1; negative without conflict: 1", text);
        Assert.Contains("Route credit: True; practice available: True", text);
        Assert.Contains("Gap: no assessment evidence yet", text);
        Assert.Contains("Reason for this assessment", text);
        Assert.Contains("source: test-model", text);
        Assert.Contains("; due", text);
        Assert.Contains("Confirmed algebra identity", text);
        Assert.Contains($"Project link: #{project.Id} Owned project", text);
        Assert.DoesNotContain("Private foreign", text);
        Assert.Contains("Next: /learn_progress 2", text);
        Assert.Contains("Previous: /learn_progress 2", text);
        Assert.DoesNotContain("Next: /learn_progress 4", text);
        Assert.Contains("No program on this page", text);
        Assert.All(programs, p => Assert.Equal(1, replies.Count(r => r.Text.Contains($"Program: {p.Id}", StringComparison.Ordinal))));
        Assert.All(replies, r => Assert.InRange(r.Text.Length, 1, 4096));
        Assert.Contains("No programs yet", Assert.Single(replies, r => r.ChatId == 303).Text);
        Assert.Equal(programs[0].Id, (await context.Set<LearningContext>().SingleAsync()).ProgramId);
        Assert.Equal(2, await context.Set<Lesson>().CountAsync());
        Assert.Equal(2, await context.Set<LearningEvidence>().CountAsync());
        Assert.Equal(5, await context.ProcessedTelegramUpdates.CountAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("bad")]
    [InlineData("2147483647")]
    [InlineData("999999999999")]
    public async Task Post_InvalidProgressPage_ReturnsUsageWithoutCreatingProgram(string argument)
    {
        using var host = new OwnDayHostFactory();
        using var client = host.CreateClient();
        await TelegramCurriculumTests.PostAsync(client, 1, 101, "/learn_progress " + argument);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Contains("Use /learn_progress [page]", (await db.TelegramOutboxMessages.SingleAsync()).Text);
        Assert.Empty(await db.Set<LearningProgram>().ToListAsync());
    }

    [Fact]
    public async Task ProgressAsync_InvalidOwnerAndCancellation_RejectsBeforeReadingReport()
    {
        using var host = new OwnDayHostFactory();
        using var client = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CurriculumService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ProgressAsync(default, 1, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProgressAsync(new(101), 1, cancellation.Token));
    }
}
