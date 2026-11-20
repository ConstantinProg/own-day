using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public static class StudyActionProvider
{
    public static IReadOnlyList<LearningAction> ForInvitation(StudyInvitation invitation) =>
        [new(LearningActionKind.StartInvitation, invitation.Id), new(LearningActionKind.PostponeInvitation, invitation.Id),
            new(LearningActionKind.SkipInvitation, invitation.Id)];

    public static IReadOnlyList<LearningAction> ForSchedule(StudySchedule? schedule) =>
        schedule is null || schedule.EffectiveReminderTypes == StudyReminderType.None ? [] :
            [new(schedule.Paused ? LearningActionKind.ResumeReminders : LearningActionKind.PauseReminders), new(LearningActionKind.StopReminders)];
}
