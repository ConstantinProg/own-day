using System.Security.Cryptography;
using System.Text;

namespace OwnDay.Domain.Learning;

public sealed record StudyWindow(TimeOnly Start, TimeOnly End);

[Flags]
public enum StudyReminderType { None = 0, Study = 1, Review = 2, OpenExercise = 4 }

public sealed class StudySchedule
{
    private StudySchedule() { }

    public UserId UserId { get; private set; }
    public string TimeZoneId { get; private set; } = string.Empty;
    public int EveryDays { get; private set; }
    public IReadOnlyList<StudyWindow> Windows { get; private set; } = [];
    public bool Paused { get; private set; }
    public DateTimeOffset NextAt { get; private set; }
    public DateTimeOffset? LastInvitedAt { get; private set; }
    public Guid? InvitationId { get; private set; }
    public Guid? ProgramId { get; private set; }
    public Guid? SkillId { get; private set; }
    public StudyReminderType? ReminderTypes { get; private set; }
    public Guid? LessonId { get; private set; }
    public string? QuestionFingerprint { get; private set; }
    public StudyReminderType EffectiveReminderTypes => ReminderTypes ?? (StudyReminderType.Study | StudyReminderType.Review);

    public static StudySchedule Create(UserId user, string zone, int everyDays,
        IReadOnlyList<StudyWindow> windows, DateTimeOffset now, StudyReminderType? reminderTypes = null)
    {
        if (!user.IsValid) throw new ArgumentException("Invalid user.", nameof(user));
        var schedule = new StudySchedule { UserId = user };
        schedule.Configure(zone, everyDays, windows, now, reminderTypes);
        return schedule;
    }

    public void Configure(string zone, int everyDays, IReadOnlyList<StudyWindow> windows, DateTimeOffset now,
        StudyReminderType? reminderTypes = null)
    {
        RequireUtc(now);
        if (zone.Length is < 1 or > 100) throw new ArgumentException("Invalid time zone.", nameof(zone));
        _ = TimeZoneInfo.FindSystemTimeZoneById(zone);
        if (everyDays is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(everyDays));
        var ordered = windows.OrderBy(w => w.Start).ToArray();
        if (ordered.Length is < 1 or > 4 || ordered.Any(w => w.Start >= w.End) ||
            ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.End > pair.Second.Start))
            throw new ArgumentException("Use one to four non-overlapping daily windows.", nameof(windows));
        const StudyReminderType all = StudyReminderType.Study | StudyReminderType.Review | StudyReminderType.OpenExercise;
        if (reminderTypes is { } types)
        {
            if (types == StudyReminderType.None || (types & ~all) != StudyReminderType.None)
                throw new ArgumentException("At least one supported reminder type is required.", nameof(reminderTypes));
            if (EffectiveReminderTypes == StudyReminderType.None) Paused = false;
            ReminderTypes = types;
        }
        TimeZoneId = zone;
        EveryDays = everyDays;
        Windows = Array.AsReadOnly(ordered);
        ClearInvitation();
        Recalculate(now);
    }

    public void SetPaused(bool paused, DateTimeOffset now)
    {
        RequireUtc(now);
        if (Paused == paused || !paused && EffectiveReminderTypes == StudyReminderType.None) return;
        Paused = paused;
        ClearInvitation();
        Recalculate(now);
    }

    public void OptOut()
    {
        ReminderTypes = StudyReminderType.None;
        Paused = true;
        ClearInvitation();
    }

    public bool Allows(StudyReminderType type) => !Paused && (EffectiveReminderTypes & type) != StudyReminderType.None;

    public bool IsDue(DateTimeOffset now) => !Paused && EffectiveReminderTypes != StudyReminderType.None && NextAt <= now;

    public bool IsWithinWindow(DateTimeOffset now)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);
        return Windows.Any(w => local >= w.Start && local < w.End);
    }

    public bool IsCurrent(Guid invitation, DateTimeOffset now) =>
        Allows(LessonId is not null ? StudyReminderType.OpenExercise : SkillId is not null ? StudyReminderType.Review : StudyReminderType.Study) &&
        InvitationId == invitation && now < NextAt;

    public Guid Invite(Guid? programId, Guid? skillId, DateTimeOffset now)
    {
        if ((programId is null) == (skillId is null) || programId == Guid.Empty || skillId == Guid.Empty)
            throw new ArgumentException("Exactly one invitation target is required.");
        BeginInvitation(skillId is null ? StudyReminderType.Study : StudyReminderType.Review, now);
        ProgramId = programId;
        SkillId = skillId;
        return InvitationId!.Value;
    }

    public Guid InviteExercise(Lesson lesson, DateTimeOffset now)
    {
        if (lesson.UserId != UserId || lesson.Status is not (LessonStatus.AwaitingAnswer or LessonStatus.Paused))
            throw new ArgumentException("Reminder requires the learner's open exercise.", nameof(lesson));
        BeginInvitation(StudyReminderType.OpenExercise, now);
        LessonId = lesson.Id;
        QuestionFingerprint = Fingerprint(lesson.ResponseQuestion);
        return InvitationId!.Value;
    }

    public bool MatchesOpenExercise(Lesson? lesson) => lesson is not null && lesson.Id == LessonId && lesson.UserId == UserId &&
        lesson.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused && QuestionFingerprint == Fingerprint(lesson.ResponseQuestion);

    private void BeginInvitation(StudyReminderType type, DateTimeOffset now)
    {
        RequireUtc(now);
        if (!Allows(type) || !IsDue(now) || !IsWithinWindow(now)) throw new InvalidOperationException("Invitation is not due or allowed.");
        ClearInvitation();
        InvitationId = Guid.NewGuid();
        LastInvitedAt = now;
        NextAt = NextWindow(now, LocalDate(now).AddDays(EveryDays));
    }

    private static string Fingerprint(string question) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(question)));

    public void Advance(DateTimeOffset now)
    {
        RequireUtc(now);
        ClearInvitation();
        NextAt = IsWithinWindow(now)
            ? NextWindow(now, LocalDate(now).AddDays(EveryDays))
            : NextWindow(now, LocalDate(now));
    }

    public void Resolve(Guid invitation, bool later, DateTimeOffset now)
    {
        RequireUtc(now);
        if (!IsCurrent(invitation, now)) throw new InvalidOperationException("Invitation has expired.");
        ClearInvitation();
        if (later) NextAt = NextWindow(now.AddHours(1), LocalDate(now.AddHours(1)));
    }

    private TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    private DateOnly LocalDate(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

    private void Recalculate(DateTimeOffset now)
    {
        var minimum = LastInvitedAt is { } last ? LocalDate(last).AddDays(EveryDays) : LocalDate(now);
        NextAt = NextWindow(now, minimum);
    }

    private DateTimeOffset NextWindow(DateTimeOffset notBefore, DateOnly minimum)
    {
        var zone = Zone;
        var date = LocalDate(notBefore);
        if (date < minimum) date = minimum;
        if (date == LocalDate(notBefore) && IsWithinWindow(notBefore)) return notBefore;
        // A DST gap can consume a whole window (or date). Skip it without sending outside the window.
        for (var day = 0; day < 370; day++, date = date.AddDays(1))
        {
            DateTimeOffset? earliest = null;
            foreach (var window in Windows)
            {
                var local = date.ToDateTime(window.Start, DateTimeKind.Unspecified);
                var end = date.ToDateTime(window.End, DateTimeKind.Unspecified);
                while (local < end && zone.IsInvalidTime(local)) local = local.AddMinutes(1);
                if (local >= end) continue;
                var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
                foreach (var candidate in offsets.Select(offset => new DateTimeOffset(local, offset).ToUniversalTime()))
                {
                    if (candidate >= notBefore && (earliest is null || candidate < earliest.Value)) earliest = candidate;
                }
            }
            if (earliest is { } next) return next;
        }
        throw new InvalidOperationException("No valid study window found.");
    }

    private void ClearInvitation()
    {
        InvitationId = null;
        ProgramId = null;
        SkillId = null;
        LessonId = null;
        QuestionFingerprint = null;
    }

    private static void RequireUtc(DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("UTC timestamp required.", nameof(now));
    }
}
