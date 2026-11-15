using OwnDay.Domain;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class StudyTextTests
{
    [Theory]
    [InlineData("напоминай мне Europe/Moscow | 1 | 18:00-20:00", "learn_schedule", "Europe/Moscow | 1 | 18:00-20:00")]
    [InlineData("НЕ ПРИСЫЛАЙ ПРИГЛАШЕНИЯ", "learn_notify_pause", "")]
    [InlineData("возобнови приглашения", "learn_notify_resume", "")]
    [InlineData("расписание занятий", "learn_schedule", "")]
    [InlineData("покажи повторения", "learn_reviews", "")]
    [InlineData("отложить приглашение stale", "learn_invite_later", "stale")]
    [InlineData("начать приглашение abc", "learn_invite_start", "abc")]
    [InlineData("пропустить приглашение abc", "learn_invite_skip", "abc")]
    public void ParseText_ExplicitPhrase_PreservesCommandArgumentsAndOwner(string text, string name, string args)
    {
        var command = TelegramStudyCommandHandler.ParseText(text, new UserId(101));
        Assert.NotNull(command);
        Assert.Equal(name, command.Name);
        Assert.Equal(args, command.Arguments);
        Assert.Equal(new UserId(101), command.UserId);
    }

    [Theory]
    [InlineData("позже")]
    [InlineData("да")]
    [InlineData("I want to study")]
    [InlineData("не присылай приглашения завтра")]
    public void ParseText_AmbiguousOrLessonText_DoesNotInventScheduleOrInvitationTarget(string text) =>
        Assert.Null(TelegramStudyCommandHandler.ParseText(text, new UserId(101)));
}
