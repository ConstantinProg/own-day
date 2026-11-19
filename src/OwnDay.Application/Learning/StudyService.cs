using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public interface IStudyScheduleStore
{
    Task<StudySchedule?> FindAsync(UserId user, CancellationToken token);
    void Add(StudySchedule schedule);
}

public sealed record StudyInvitation(Guid Id, string Topic, bool IsReview, bool IsOpenExercise = false);

public sealed class StudyService(IStudyScheduleStore store, ILessonStore lessons, ICurriculumStore programs,
    IKnowledgeStore knowledge, LearningService learning, CurriculumService curriculum, TimeProvider clock)
{
    // The delivery adapter reads these facts under the learner lock, immediately before sending.
    public static bool CanDeliver(StudySchedule? schedule, Guid invitation, DateTimeOffset now,
        Lesson? active, ReviewSchedule? review, Guid? selectedProgram)
    {
        if (schedule is null || !schedule.IsCurrent(invitation, now) || !schedule.IsWithinWindow(now)) return false;
        if (schedule.LessonId is not null) return schedule.MatchesOpenExercise(active);
        if (active is not null) return false;
        return schedule.SkillId is { } skill ? review is not null && review.UserId == schedule.UserId &&
            review.SkillId == skill && review.IsDue(now) : schedule.ProgramId is not null && schedule.ProgramId == selectedProgram;
    }

    public Task<StudySchedule?> ShowAsync(UserId user, CancellationToken token)
    {
        Validate(user, token);
        return store.FindAsync(user, token);
    }

    public async Task<StudySchedule> ConfigureAsync(UserId user, string zone, int everyDays,
        IReadOnlyList<StudyWindow> windows, CancellationToken token, StudyReminderType? reminderTypes = null)
    {
        Validate(user, token);
        var schedule = await store.FindAsync(user, token);
        token.ThrowIfCancellationRequested();
        if (schedule is null)
        {
            schedule = StudySchedule.Create(user, zone, everyDays, windows, clock.GetUtcNow(), reminderTypes);
            store.Add(schedule);
        }
        else schedule.Configure(zone, everyDays, windows, clock.GetUtcNow(), reminderTypes);
        return schedule;
    }

    public async Task<StudySchedule?> PauseAsync(UserId user, bool paused, CancellationToken token)
    {
        var schedule = await ShowAsync(user, token);
        token.ThrowIfCancellationRequested();
        schedule?.SetPaused(paused, clock.GetUtcNow());
        return schedule;
    }

    public async Task<StudySchedule?> OptOutAsync(UserId user, CancellationToken token)
    {
        var schedule = await ShowAsync(user, token);
        token.ThrowIfCancellationRequested();
        schedule?.OptOut();
        return schedule;
    }

    // The caller owns the user lock and the transaction containing schedule changes and delivery intent.
    public async Task<StudyInvitation?> PlanAsync(UserId user, CancellationToken token)
    {
        var schedule = await ShowAsync(user, token);
        var now = clock.GetUtcNow();
        if (schedule is null || !schedule.IsDue(now)) return null;
        if (!schedule.IsWithinWindow(now))
        {
            token.ThrowIfCancellationRequested();
            schedule.Advance(now);
            return null;
        }
        var active = await lessons.FindActiveAsync(user, token);
        token.ThrowIfCancellationRequested();
        if (active is not null)
        {
            if (schedule.Allows(StudyReminderType.OpenExercise))
                return new(schedule.InviteExercise(active, now), active.Topic, false, IsOpenExercise: true);
            schedule.Advance(now);
            return null;
        }
        if (schedule.Allows(StudyReminderType.Review))
        {
            var reviews = await knowledge.DueReviewsAsync(user, now, 0, 1, token);
            token.ThrowIfCancellationRequested();
            if (reviews.FirstOrDefault() is { } review)
                return new(schedule.Invite(null, review.Skill.Id, now), review.Skill.Title, true);
        }
        if (schedule.Allows(StudyReminderType.Study))
        {
            var program = await programs.FindAsync(user, token);
            if (program is not null && !program.DiagnosticPending)
            {
                var completed = await programs.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
                token.ThrowIfCancellationRequested();
                if (program.NextTopic(completed) is not null)
                    return new(schedule.Invite(program.Id, null, now), program.Discipline, false);
            }
        }
        token.ThrowIfCancellationRequested();
        schedule.Advance(now);
        return null;
    }

    public async Task<bool> DismissAsync(UserId user, Guid invitation, bool later, CancellationToken token)
    {
        var schedule = await ShowAsync(user, token);
        token.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        if (schedule is null || !schedule.IsCurrent(invitation, now)) return false;
        schedule.Resolve(invitation, later, now);
        return true;
    }

    public async Task<CurriculumResult?> StartAsync(UserId user, Guid invitation, CancellationToken token)
    {
        var schedule = await ShowAsync(user, token);
        var now = clock.GetUtcNow();
        if (schedule is null || !schedule.IsCurrent(invitation, now)) return null;
        CurriculumResult result;
        Guid? selectedProgramId = null;
        var active = await lessons.FindActiveAsync(user, token);
        if (schedule.LessonId is not null)
        {
            if (!schedule.MatchesOpenExercise(active)) return null;
            result = new CurriculumResult.Lesson(await learning.ResumeAsync(user, token));
        }
        else if (active is not null)
            result = new CurriculumResult.Lesson(await learning.ResumeAsync(user, token));
        else if (schedule.SkillId is { } skill)
            result = new CurriculumResult.Lesson(await learning.ReviewAsync(user, skill, token));
        else
        {
            // Do not change the selected program until the material has succeeded.
            // Preparation and provider failure must leave the previous selection intact.
            result = await curriculum.NextAsync(user, schedule.ProgramId!.Value, token);
            if (result is CurriculumResult.ProgramNotFound) return result;
            selectedProgramId = schedule.ProgramId;
        }
        token.ThrowIfCancellationRequested();
        // Provider failures leave the same invitation available for an explicit retry.
        if (result is not (CurriculumResult.ProviderUnavailable or
            CurriculumResult.Lesson { Value: LearningResult.ProviderUnavailable }))
        {
            if (selectedProgramId is { } programId)
            {
                var program = await programs.FindAsync(user, programId, token);
                if (program is not null) await programs.SelectAsync(program, token);
            }
            schedule.Resolve(invitation, false, now);
        }
        return result;
    }

    private static void Validate(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
    }
}
