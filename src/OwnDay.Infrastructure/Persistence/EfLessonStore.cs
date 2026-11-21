using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfLessonStore(OwnDayDbContext db) : ILessonStore
{
    public async Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var local = db.Set<Lesson>().Local.SingleOrDefault(lesson => lesson.UserId == user && db.Entry(lesson).State is EntityState.Added or EntityState.Modified &&
            lesson.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused);
        if (local is not null) return local;
        var stored = await db.Set<Lesson>().Include(l => l.Interactions.OrderByDescending(i => i.Kind == LessonInteractionKind.FullAnswer)
            .ThenByDescending(i => i.Id).Take(10)).SingleOrDefaultAsync(l => l.UserId == user &&
            (l.Status == LessonStatus.AwaitingAnswer || l.Status == LessonStatus.Paused), token);
        return stored?.Status is LessonStatus.AwaitingAnswer or LessonStatus.Paused ? stored : null;
    }

    public Task<Lesson?> FindLatestAssessedAsync(UserId user, Guid? programId, CancellationToken token) =>
        (from evidence in db.Set<LearningEvidence>()
         join lesson in db.Set<Lesson>() on new { evidence.LessonId, evidence.UserId } equals new { LessonId = lesson.Id, lesson.UserId }
         where evidence.UserId == user && lesson.Status == LessonStatus.Completed && (lesson.ProgramId ?? lesson.PracticeProgramId) == programId
         orderby evidence.ObservedAt descending, lesson.Id descending
         select lesson).FirstOrDefaultAsync(token);

    public Task<string?> FindPreviousChoiceQuestionAsync(UserId user, Guid? programId, string topic, CancellationToken token) =>
        (from evidence in db.Set<LearningEvidence>()
         join lesson in db.Set<Lesson>() on new { evidence.LessonId, evidence.UserId } equals new { LessonId = lesson.Id, lesson.UserId }
         where evidence.UserId == user && lesson.Status == LessonStatus.Completed && lesson.ProgramId == null &&
               lesson.PracticeProgramId == programId && lesson.Topic == topic
         orderby evidence.ObservedAt descending, lesson.Id descending
         select lesson.Choice == null ? null : lesson.Question).FirstOrDefaultAsync(token);

    public async Task<string[]> RecentQuestionsAsync(UserId user, Guid skillId, CancellationToken token)
    {
        var stored = await (from evidence in db.Set<LearningEvidence>()
         join lesson in db.Set<Lesson>() on new { evidence.LessonId, evidence.UserId } equals new { LessonId = lesson.Id, lesson.UserId }
         where evidence.UserId == user && evidence.SkillId == skillId
         orderby evidence.ObservedAt descending, lesson.Id descending
            select lesson.Question).Take(3).ToArrayAsync(token);
        var justAssessed = db.Set<Lesson>().Local.Where(lesson => lesson.UserId == user && lesson.SkillId == skillId &&
            lesson.Status == LessonStatus.Completed && db.Entry(lesson).State == EntityState.Modified &&
            (LessonStatus)db.Entry(lesson).OriginalValues[nameof(Lesson.Status)]! != LessonStatus.Completed);
        return justAssessed.Select(lesson => lesson.Question).Concat(stored).Take(3).ToArray();
    }

    public void Add(Lesson lesson) => db.Set<Lesson>().Add(lesson);

    public async Task<bool> ChoiceQuestionSeenAsync(UserId user, string question, CancellationToken token)
    {
        var fingerprint = RubricEvidence.FingerprintQuestion(question);
        var questions = db.Set<Lesson>().Where(lesson => lesson.UserId == user && lesson.Choice != null)
            .Select(lesson => lesson.Question).AsAsyncEnumerable();
        await foreach (var previous in questions.WithCancellation(token))
            if (RubricEvidence.FingerprintQuestion(previous) == fingerprint) return true;
        return false;
    }
}
