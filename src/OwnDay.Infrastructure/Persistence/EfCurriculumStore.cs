using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfCurriculumStore(OwnDayDbContext db) : ICurriculumStore
{
    public Task<Lesson?> FindSuspendedLessonAsync(UserId user, Guid programId, CancellationToken token) =>
        db.Set<Lesson>().Include(lesson => lesson.Interactions.OrderByDescending(interaction => interaction.Id).Take(10))
            .SingleOrDefaultAsync(lesson => lesson.UserId == user && lesson.SuspendedForProgramId == programId &&
                lesson.Status == LessonStatus.Suspended, token);

    public async Task FlushAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Program suspension requires the caller's atomic transaction.");
        await db.SaveChangesAsync(token);
    }
    public Task<LearningContext?> FindContextAsync(UserId user, CancellationToken token) =>
        db.Set<LearningContext>().SingleOrDefaultAsync(context => context.UserId == user, token);

    public Task<Lesson?> FindPracticeLessonAsync(UserId user, Guid lessonId, CancellationToken token) =>
        db.Set<Lesson>().Include(lesson => lesson.Interactions.OrderByDescending(interaction => interaction.Id).Take(10))
            .SingleOrDefaultAsync(lesson => lesson.UserId == user && lesson.Id == lessonId, token);

    public Task<Lesson?> FindLastProgramLessonAsync(UserId user, Guid programId, CancellationToken token, string? excludingTopic = null) =>
        (from lesson in db.Set<Lesson>() join evidence in db.Set<LearningEvidence>() on lesson.Id equals evidence.LessonId
         where lesson.UserId == user && evidence.UserId == user && lesson.ProgramId == programId && lesson.Status == LessonStatus.Completed && lesson.SkillId != null &&
               (excludingTopic == null || lesson.TopicCode != excludingTopic)
         orderby evidence.ObservedAt descending, lesson.Id descending
         select lesson).FirstOrDefaultAsync(token);
    public Task<LearningProgram?> FindAsync(UserId user, CancellationToken token) =>
        db.Set<LearningProgram>().Include(p => p.Routes).SingleOrDefaultAsync(p => p.UserId == user &&
            db.Set<LearningContext>().Any(c => c.UserId == user && c.ProgramId == p.Id), token);

    public Task<LearningProgram?> FindAsync(UserId user, Guid programId, CancellationToken token) =>
        db.Set<LearningProgram>().Include(p => p.Routes).SingleOrDefaultAsync(p => p.UserId == user && p.Id == programId, token);

    public async Task<IReadOnlyList<LearningProgram>> ListAsync(UserId user, int offset, int limit, CancellationToken token) =>
        await db.Set<LearningProgram>().Include(p => p.Routes).Where(p => p.UserId == user)
            .OrderBy(p => p.Id).Skip(offset).Take(limit).ToListAsync(token);

    public async Task SelectAsync(LearningProgram program, CancellationToken token)
    {
        var context = await db.Set<LearningContext>().SingleOrDefaultAsync(c => c.UserId == program.UserId, token);
        token.ThrowIfCancellationRequested();
        if (context is null) db.Set<LearningContext>().Add(LearningContext.Create(program));
        else context.Select(program);
    }

    public async Task<IReadOnlySet<string>> CompletedTopicsAsync(UserId user, Guid programId, int version, CancellationToken token)
    {
        var program = await FindAsync(user, programId, token);
        if (program is null) return new HashSet<string>(StringComparer.Ordinal);
        var versions = program.ProgressVersions(version);
        var stored = await db.Set<Lesson>().Where(l => l.UserId == user && l.ProgramId == programId && l.RouteVersion != null &&
            versions.Contains(l.RouteVersion.Value) && l.Status == LessonStatus.Completed && l.Choice == null &&
            !db.Set<LessonInteraction>().Any(interaction => interaction.LessonId == l.Id && interaction.Kind == LessonInteractionKind.FullAnswer) &&
            (db.Set<LearningEvidence>().Where(evidence => evidence.UserId == user && evidence.LessonId == l.Id)
                .Select(evidence => evidence.ReassessmentCredit).FirstOrDefault() ?? l.IsCorrect) == true)
            .Select(l => l.TopicCode!).ToListAsync(token);
        // The answer and the next exercise are committed together by the worker.
        var justAssessed = db.Set<Lesson>().Local.Where(l => l.UserId == user && l.ProgramId == programId &&
            l.RouteVersion is { } routeVersion && versions.Contains(routeVersion) && l.Status == LessonStatus.Completed &&
            l.Choice is null && l.IsCorrect == true && l.Interactions.All(i => i.Kind != LessonInteractionKind.FullAnswer) &&
            db.Entry(l).State == EntityState.Modified && (LessonStatus)db.Entry(l).OriginalValues[nameof(Lesson.Status)]! != LessonStatus.Completed);
        return stored.Concat(justAssessed.Select(l => l.TopicCode!)).ToHashSet(StringComparer.Ordinal);
    }

    public void Add(LearningProgram program) => db.Set<LearningProgram>().Add(program);
}
