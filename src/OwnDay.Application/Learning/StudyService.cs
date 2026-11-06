using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Learning;

public interface IStudyScheduleStore
{
    Task<StudySchedule?> FindAsync(UserId user, CancellationToken token);
    void Add(StudySchedule schedule);
}

public sealed record StudyInvitation(Guid Id, string Topic, bool IsReview);

public sealed class StudyService(IStudyScheduleStore store, ILessonStore lessons, ICurriculumStore programs,
    IKnowledgeStore knowledge, LearningService learning, CurriculumService curriculum, TimeProvider clock)
{
    // The delivery adapter reads these facts under the learner lock, immediately before sending.
    public static bool CanDeliver(StudySchedule? schedule, Guid invitation, DateTimeOffset now,
        bool hasActiveLesson, ReviewSchedule? review) =>
        schedule is not null && schedule.IsCurrent(invitation, now) && schedule.IsWithinWindow(now) &&
        !hasActiveLesson && (schedule.SkillId is null || review is not null &&
            review.UserId == schedule.UserId && review.SkillId == schedule.SkillId && review.IsDue(now));

    public Task<StudySchedule?> ShowAsync(UserId user, CancellationToken token)
    {
        Validate(user, token);
        return store.FindAsync(user, token);
    }

    public async Task<StudySchedule> ConfigureAsync(UserId user, string zone, int everyDays,
        IReadOnlyList<StudyWindow> windows, CancellationToken token)
    {
        Validate(user, token);
        var schedule = await store.FindAsync(user, token);
        token.ThrowIfCancellationRequested();
        if (schedule is null)
        {
            schedule = StudySchedule.Create(user, zone, everyDays, windows, clock.GetUtcNow());
            store.Add(schedule);
        }
        else schedule.Configure(zone, everyDays, windows, clock.GetUtcNow());
        return schedule;
    }

    public async Task<StudySchedule?> PauseAsync(UserId user, bool paused, CancellationToken token)
    {
        var schedule = await ShowAsync(user, token);
        token.ThrowIfCancellationRequested();
        schedule?.SetPaused(paused, clock.GetUtcNow());
        return schedule;
    }

    // The caller owns the user lock and the transaction containing schedule changes and delivery intent.
    public async Task<StudyInvitation?> PlanAsync(UserId user, CancellationToken token)
    {
        var schedule = await ShowAsync(user, token);
        var now = clock.GetUtcNow();
        if (schedule is null || !schedule.IsDue(now)) return null;
        if (!schedule.IsWithinWindow(now) || await lessons.FindActiveAsync(user, token) is not null)
        {
            token.ThrowIfCancellationRequested();
            schedule.Advance(now);
            return null;
        }
        var reviews = await knowledge.DueReviewsAsync(user, now, 0, 1, token);
        token.ThrowIfCancellationRequested();
        if (reviews.FirstOrDefault() is { } review)
            return new(schedule.Invite(null, review.Skill.Id, now), review.Skill.Title, true);
        var program = await programs.FindAsync(user, token);
        if (program is not null && !program.DiagnosticPending)
        {
            var completed = await programs.CompletedTopicsAsync(user, program.Id, program.CurrentRoute.Version, token);
            token.ThrowIfCancellationRequested();
            if (program.NextTopic(completed) is not null)
                return new(schedule.Invite(program.Id, null, now), program.Discipline, false);
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
        if (await lessons.FindActiveAsync(user, token) is not null)
            result = new CurriculumResult.Lesson(await learning.ResumeAsync(user, token));
        else if (schedule.SkillId is { } skill)
            result = new CurriculumResult.Lesson(await learning.ReviewAsync(user, skill, token));
        else
        {
            var selected = await curriculum.SelectAsync(user, schedule.ProgramId!.Value, token);
            if (selected is not CurriculumResult.Program) return selected;
            result = await curriculum.NextAsync(user, token);
        }
        token.ThrowIfCancellationRequested();
        // Provider failures leave the same invitation available for an explicit retry.
        if (result is not (CurriculumResult.ProviderUnavailable or
            CurriculumResult.Lesson { Value: LearningResult.ProviderUnavailable }))
            schedule.Resolve(invitation, false, now);
        return result;
    }

    private static void Validate(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
    }
}
