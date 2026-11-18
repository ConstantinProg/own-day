namespace OwnDay.Domain.Learning;

public enum LearningSessionEnd { Idle, Explicit, ProgramChanged }

public sealed record LearningSessionSummary(Guid LessonId, int RouteVersion, string Topic, string Question);

public sealed class LearningSession
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromHours(2);
    private static readonly TimeSpan _activityGap = TimeSpan.FromMinutes(5);

    private LearningSession() { }

    public Guid Id { get; private set; }
    public UserId UserId { get; private set; }
    public Guid ProgramId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public LearningSessionEnd? EndReason { get; private set; }
    public long ActiveSeconds { get; private set; }
    public long FirstSourceOrder { get; private set; }
    public LearningSessionSummary? Summary { get; private set; }

    public static LearningSession Start(UserId user, Guid programId, DateTimeOffset at, long sourceOrder, Lesson? lesson)
    {
        if (!user.IsValid || programId == Guid.Empty || sourceOrder <= 0)
            throw new ArgumentException("A session requires an owner, selected program, and source order.");
        var session = new LearningSession
        {
            Id = Guid.NewGuid(), UserId = user, ProgramId = programId, StartedAt = at.ToUniversalTime(),
            LastActivityAt = at.ToUniversalTime(), FirstSourceOrder = sourceOrder
        };
        session.UpdateSummary(lesson);
        return session;
    }

    public bool IsIdle(DateTimeOffset at) => EndedAt is null && at - LastActivityAt >= IdleLimit;

    public void RecordActivity(DateTimeOffset at, Lesson? lesson)
    {
        if (EndedAt is not null || IsIdle(at)) throw new InvalidOperationException("Start a new session.");
        if (at < LastActivityAt) return;
        UpdateSummary(lesson);
        var gap = at - LastActivityAt;
        if (gap <= _activityGap) ActiveSeconds += (long)gap.TotalSeconds;
        LastActivityAt = at.ToUniversalTime();
    }

    public void End(DateTimeOffset at, LearningSessionEnd reason, Lesson? lesson = null)
    {
        if (EndedAt is not null) return;
        if (!Enum.IsDefined(reason) || at < LastActivityAt || reason == LearningSessionEnd.Idle && !IsIdle(at))
            throw new ArgumentException("Invalid session end.");
        UpdateSummary(lesson);
        EndedAt = reason == LearningSessionEnd.Idle ? LastActivityAt.Add(IdleLimit) : at.ToUniversalTime();
        EndReason = reason;
    }

    private void UpdateSummary(Lesson? lesson)
    {
        if (lesson is null || lesson.ProgramId != ProgramId) return;
        if (lesson.UserId != UserId) throw new ArgumentException("Foreign lesson.", nameof(lesson));
        Summary = new(lesson.Id, lesson.RouteVersion!.Value, lesson.Topic, lesson.ResponseQuestion);
    }
}
