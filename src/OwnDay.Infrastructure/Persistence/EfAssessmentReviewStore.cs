using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfAssessmentReviewStore(OwnDayDbContext db) : IAssessmentReviewStore
{
    private IQueryable<AssessmentAttempt> Attempts(UserId user, Guid? lessonId, Guid? programId) =>
        from evidence in db.Set<LearningEvidence>()
        join lesson in db.Set<Lesson>() on new { evidence.LessonId, evidence.UserId } equals new { LessonId = lesson.Id, lesson.UserId }
        where evidence.UserId == user && lesson.Status == LessonStatus.Completed &&
            (lessonId == null || lesson.Id == lessonId) && (programId == null || lesson.ProgramId == programId)
        orderby evidence.ObservedAt descending, lesson.Id descending
        select new AssessmentAttempt(lesson, evidence);

    public Task<AssessmentAttempt?> FindAsync(UserId user, Guid lessonId, CancellationToken token) =>
        Attempts(user, lessonId, null).SingleOrDefaultAsync(token);

    public Task<AssessmentAttempt?> FindLatestAsync(UserId user, Guid? programId, CancellationToken token) =>
        Attempts(user, null, programId).FirstOrDefaultAsync(token);
}
