using Microsoft.EntityFrameworkCore;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_NaturalInvitationStartDuringPractice_UsesSameGuardAsCommand()
    {
        await SeedAsync("learn_ask");
        await using (var db = Open())
        {
            (await db.Set<Lesson>().SingleAsync()).Finish();
            (await db.Set<LearningContext>().SingleAsync()).BeginPractice(2);
            await db.SaveChangesAsync();
        }
        var probe = new Probe("");
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "начать приглашение " + Guid.NewGuid());
        await using var result = Open();
        Assert.Contains(await result.TelegramOutboxMessages.Select(value => value.Text).ToListAsync(),
            value => value.Contains("Сначала завершите текущую ограниченную практику", StringComparison.Ordinal));
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData("отложить приглашение")]
    [InlineData("пропустить приглашение")]
    public async Task Post_NaturalInvitationAction_ConsumesOnlyCurrentOwnedInvitation(string phrase)
    {
        await SeedAsync("learn_ask");
        Guid invitation;
        await using (var db = Open())
        {
            var program = await db.Set<LearningProgram>().SingleAsync();
            var now = DateTimeOffset.UtcNow;
            var schedule = StudySchedule.Create(_user, "UTC", 1,
                [new(new TimeOnly(0, 0), new TimeOnly(23, 59))], now);
            invitation = schedule.Invite(program.Id, null, now);
            db.Add(schedule);
            await db.SaveChangesAsync();
        }
        var probe = new Probe("");
        using var host = DialogueHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, phrase + " " + Guid.NewGuid());
        await using (var db = Open()) Assert.Equal(invitation, (await db.Set<StudySchedule>().SingleAsync()).InvitationId);
        await SendEnglishAsync(client, 2, phrase + " " + invitation);
        await SendEnglishAsync(client, 3, phrase + " " + invitation);
        await using var result = Open();
        Assert.Null((await result.Set<StudySchedule>().SingleAsync()).InvitationId);
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Post_NaturalStudyScheduleAndOptOut_PreservesCurrentQuestionAcrossRestart()
    {
        await SeedAsync("learn_ask");
        var probe = new Probe("");
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "напоминай мне Europe/Moscow | 1 | 18:00-20:00");
            await using var db = Open();
            var schedule = await db.Set<StudySchedule>().SingleAsync();
            Assert.False(schedule.Paused);
            Assert.Equal("Europe/Moscow", schedule.TimeZoneId);
            Assert.Equal(LessonStatus.AwaitingAnswer, (await db.Set<Lesson>().SingleAsync()).Status);
        }
        using (var host = DialogueHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 2, "не присылай приглашения");
            await using (var db = Open()) Assert.True((await db.Set<StudySchedule>().SingleAsync()).Paused);
            await SendEnglishAsync(client, 3, "возобнови приглашения");
            await using var result = Open();
            Assert.False((await result.Set<StudySchedule>().SingleAsync()).Paused);
            Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
            Assert.Equal(0, probe.Calls);
        }
    }
}
