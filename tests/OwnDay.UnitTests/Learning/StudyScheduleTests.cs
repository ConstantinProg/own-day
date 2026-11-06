using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class StudyScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);
    private static readonly StudyWindow[] Windows = [new(new(18, 0), new(20, 0))];

    [Fact]
    public void Invite_LocalFrequency_UsesActualDateAndDoesNotCatchUpMissedDays()
    {
        var schedule = StudySchedule.Create(new(1), "Europe/Moscow", 3, Windows, Now.AddDays(-20));
        var id = schedule.Invite(Guid.NewGuid(), null, Now);
        Assert.Equal(Now.AddDays(3), schedule.NextAt);
        Assert.True(schedule.IsCurrent(id, Now));
        Assert.False(schedule.IsDue(Now));
        Assert.Throws<InvalidOperationException>(() => schedule.Invite(Guid.NewGuid(), null, Now));
        Assert.False(schedule.IsCurrent(id, schedule.NextAt));
    }

    [Fact]
    public void Configure_ZoneChange_InvalidatesInvitationAndPreservesFrequency()
    {
        var schedule = StudySchedule.Create(new(1), "Europe/Moscow", 1, Windows, Now);
        var id = schedule.Invite(Guid.NewGuid(), null, Now);
        schedule.Configure("America/New_York", 1, Windows, Now);
        Assert.False(schedule.IsCurrent(id, Now));
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 23, 0, 0, TimeSpan.Zero), schedule.NextAt);
        Assert.Equal(Now, schedule.LastInvitedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_SkipOrLater_ConsumesIdOnlyOnce(bool later)
    {
        var schedule = StudySchedule.Create(new(1), "Europe/Moscow", 1, Windows, Now);
        var id = schedule.Invite(null, Guid.NewGuid(), Now);
        schedule.Resolve(id, later, Now.AddMinutes(90));
        Assert.Null(schedule.InvitationId);
        Assert.Equal(Now.AddDays(1), schedule.NextAt);
        Assert.Throws<InvalidOperationException>(() => schedule.Resolve(id, later, Now.AddMinutes(90)));
    }

    [Fact]
    public void Resolve_LaterInsideWindow_SchedulesAtLeastOneHourLater()
    {
        var schedule = StudySchedule.Create(new(1), "Europe/Moscow", 1, Windows, Now);
        var id = schedule.Invite(null, Guid.NewGuid(), Now);
        schedule.Resolve(id, true, Now);
        Assert.Equal(Now.AddHours(1), schedule.NextAt);
        Assert.False(schedule.IsDue(Now.AddMinutes(59)));
    }

    [Fact]
    public void SetPaused_ResumeAfterLongPause_DiscardsBacklogAndOldInvitation()
    {
        var schedule = StudySchedule.Create(new(1), "Europe/Moscow", 1, Windows, Now);
        var id = schedule.Invite(null, Guid.NewGuid(), Now);
        schedule.SetPaused(true, Now);
        Assert.False(schedule.IsDue(Now.AddDays(100)));
        schedule.SetPaused(false, Now.AddDays(100).AddHours(5));
        Assert.False(schedule.IsCurrent(id, Now));
        Assert.Equal(Now.AddDays(101), schedule.NextAt);
    }

    [Fact]
    public void Advance_DelayedOutsideWindow_MovesToNextWindowWithoutInvitation()
    {
        var schedule = StudySchedule.Create(new(1), "Europe/Moscow", 1, Windows, Now);
        schedule.Advance(Now.AddDays(12).AddHours(3));
        Assert.Equal(Now.AddDays(13), schedule.NextAt);
        Assert.Null(schedule.InvitationId);
    }

    [Fact]
    public void Create_MultipleWindows_ChoosesNextAllowedWindow()
    {
        var schedule = StudySchedule.Create(new(1), "UTC", 1,
            [new(new(9, 0), new(10, 0)), new(new(18, 0), new(20, 0))], Now);
        Assert.Equal(Now.AddHours(3), schedule.NextAt);
        Assert.False(schedule.IsWithinWindow(Now));
        Assert.True(schedule.IsWithinWindow(Now.AddHours(3)));
        Assert.False(schedule.IsWithinWindow(Now.AddHours(5)));
    }

    [Theory]
    [InlineData(8, 2, 30, 4, 0, 8, 7)]
    [InlineData(8, 2, 0, 2, 30, 9, 6)]
    [InlineData(1, 1, 30, 2, 30, 1, 5)]
    public void Create_DaylightSavingGapOrOverlap_UsesValidUtcInstant(int day, int startHour, int startMinute,
        int endHour, int endMinute, int expectedDay, int expectedHour)
    {
        var month = day == 1 ? 11 : 3;
        var schedule = StudySchedule.Create(new(1), "America/New_York", 1,
            [new(new(startHour, startMinute), new(endHour, endMinute))],
            new(2026, month, day, 0, 0, 0, TimeSpan.Zero));
        var expectedMinute = day == 1 ? 30 : 0;
        Assert.Equal(new DateTimeOffset(2026, month, expectedDay, expectedHour, expectedMinute, 0, TimeSpan.Zero), schedule.NextAt);
    }

    [Theory]
    [InlineData(5, 25, 5, 30)]
    [InlineData(5, 45, 6, 10)]
    [InlineData(6, 25, 6, 30)]
    public void Create_MultipleWindowsDuringDaylightSavingOverlap_ChoosesEarliestUtcInstant(
        int hour, int minute, int expectedHour, int expectedMinute)
    {
        var schedule = StudySchedule.Create(new(1), "America/New_York", 1,
            [new(new(1, 10), new(1, 20)), new(new(1, 30), new(1, 40))],
            new(2026, 11, 1, hour, minute, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 11, 1, expectedHour, expectedMinute, 0, TimeSpan.Zero), schedule.NextAt);
        Assert.True(schedule.IsWithinWindow(schedule.NextAt));
    }

    [Theory]
    [InlineData(0, 18, 20)]
    [InlineData(31, 18, 20)]
    [InlineData(1, 20, 18)]
    [InlineData(1, 18, 18)]
    public void Configure_InvalidFrequencyOrWindow_PreservesExistingSchedule(int days, int start, int end)
    {
        var schedule = StudySchedule.Create(new(1), "Europe/Moscow", 1, Windows, Now);
        var id = schedule.Invite(null, Guid.NewGuid(), Now);
        Assert.ThrowsAny<ArgumentException>(() => schedule.Configure("UTC", days, [new(new(start, 0), new(end, 0))], Now));
        Assert.Equal("Europe/Moscow", schedule.TimeZoneId);
        Assert.Equal(id, schedule.InvitationId);
    }

    [Fact]
    public void Configure_OverlappingWindowsOrInvalidZone_RejectsSettings()
    {
        Assert.Throws<ArgumentException>(() => StudySchedule.Create(new(1), "UTC", 1,
            [new(new(9, 0), new(11, 0)), new(new(10, 0), new(12, 0))], Now));
        Assert.Throws<TimeZoneNotFoundException>(() => StudySchedule.Create(new(1), "missing/timezone", 1, Windows, Now));
        Assert.Throws<ArgumentException>(() => StudySchedule.Create(default, "UTC", 1, Windows, Now));
    }
}
