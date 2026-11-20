using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Application.TesterMode;
using OwnDay.Domain;
using OwnDay.Domain.Actions;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Persistence;

public sealed class EfTesterModeStore(OwnDayDbContext db, ICurriculumStore curriculum) : ITesterModeStore
{
    public Task<TesterModeState?> FindAsync(UserId userId, CancellationToken cancellationToken) =>
        db.Set<TesterModeState>().SingleOrDefaultAsync(state => state.UserId == userId, cancellationToken);

    public void Add(TesterModeState state) => db.Add(state);

    public async Task<TesterModeStatus> ReadStatusAsync(UserId userId, CancellationToken cancellationToken)
    {
        var tasks = await db.Actions.Where(task => task.UserId == userId).GroupBy(task => task.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Status, group => group.Count, cancellationToken);
        var lessons = await db.Set<Lesson>().Where(lesson => lesson.UserId == userId).GroupBy(lesson => lesson.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Status, group => group.Count, cancellationToken);
        var programs = await db.Set<LearningProgram>().CountAsync(program => program.UserId == userId, cancellationToken);
        var selected = await curriculum.FindAsync(userId, cancellationToken);
        int? completedTopics = null;
        if (selected is not null)
        {
            var route = selected.CurrentRoute;
            var completed = await curriculum.CompletedTopicsAsync(userId, selected.Id, route.Version, cancellationToken);
            completedTopics = route.Topics.Count(topic => completed.Contains(topic.Code));
        }
        var dialogue = await db.Set<DialogueContext>().Where(context => context.UserId == userId)
            .Select(context => new { context.Phase, context.ExpectedResponse }).SingleOrDefaultAsync(cancellationToken);
        var queued = await db.Set<LearningRequest>().CountAsync(request =>
            request.UserId == userId && request.Status != LearningRequestStatus.Completed, cancellationToken);
        return new(tasks.GetValueOrDefault(ActionStatus.Active), tasks.GetValueOrDefault(ActionStatus.Completed), programs,
            completedTopics, selected?.CurrentRoute.Topics.Count, lessons.GetValueOrDefault(LessonStatus.AwaitingAnswer),
            lessons.GetValueOrDefault(LessonStatus.Paused), lessons.GetValueOrDefault(LessonStatus.Suspended),
            dialogue?.Phase, dialogue?.ExpectedResponse, queued);
    }

    public async Task ClearUserDataAsync(UserId userId, TesterModeOperation operation, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Tester Mode data removal requires the Telegram owner transaction.");

        await EfOpenAIRequestLedger.PreserveOwnerChargesAsync(db, userId.Value, cancellationToken);
        await ClearRequestsAndRepliesAsync(userId, cancellationToken);
        await ClearLearningAsync(userId, cancellationToken);
        await ClearInboxAndTasksAsync(userId, cancellationToken);
        if (operation == TesterModeOperation.DeleteAll)
            await db.InboxOperationEvents.Where(item => item.UserId == userId.Value).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task ClearRequestsAndRepliesAsync(UserId userId, CancellationToken token)
    {
        var requests = db.Set<LearningRequest>().Where(request => request.UserId == userId);
        var requestIds = requests.Select(request => request.Id);
        await db.TelegramOutboxMessages.Where(message => message.LearningRequestId.HasValue
            ? requestIds.Contains(message.LearningRequestId.Value) : message.ChatId == userId.Value).ExecuteDeleteAsync(token);
        await db.Set<LearningAppliedOperation>().Where(operation => requestIds.Contains(operation.RequestId)).ExecuteDeleteAsync(token);
        await db.Set<LearningProviderAttempt>().Where(attempt => requestIds.Contains(attempt.RequestId)).ExecuteDeleteAsync(token);
        await db.Set<LearningProviderStep>().Where(step => requestIds.Contains(step.RequestId)).ExecuteDeleteAsync(token);
        await requests.ExecuteDeleteAsync(token);
    }

    private async Task ClearLearningAsync(UserId userId, CancellationToken token)
    {
        await db.Set<StudySchedule>().Where(schedule => schedule.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<LearningSession>().Where(session => session.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<LearningContext>().Where(context => context.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<DialogueContext>().Where(context => context.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<LearningPreference>().Where(preference => preference.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<ReviewSchedule>().Where(review => review.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<ProgramSkillLink>().Where(link => link.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<LearningEvidence>().Where(evidence => evidence.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<Lesson>().Where(lesson => lesson.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<LearningProgram>().Where(program => program.UserId == userId).ExecuteDeleteAsync(token);
        await db.Set<Skill>().Where(skill => skill.UserId == userId).ExecuteDeleteAsync(token);
    }

    private async Task ClearInboxAndTasksAsync(UserId userId, CancellationToken token)
    {
        await db.TelegramUserLanguages.Where(language => language.UserId == userId.Value).ExecuteDeleteAsync(token);
        await db.TelegramInboxDrafts.Where(draft => draft.UserId == userId.Value).ExecuteDeleteAsync(token);
        await db.InboxAutomaticApplications.Where(application => application.UserId == userId).ExecuteDeleteAsync(token);
        await db.InboxAnalysisJobs.Where(job => job.UserId == userId).ExecuteDeleteAsync(token);
        await db.InboxAnalyses.Where(analysis => analysis.UserId == userId).ExecuteDeleteAsync(token);
        // PostgreSQL and SQLite cascade the item-owned targets and Telegram destinations.
        await db.Database.ExecuteSqlAsync($"DELETE FROM inbox_items WHERE user_id = {userId.Value}", token);
        await db.Actions.Where(task => task.UserId == userId).ExecuteDeleteAsync(token);
        await db.SomedayMaybes.Where(item => item.UserId == userId).ExecuteDeleteAsync(token);
        await db.References.Where(item => item.UserId == userId).ExecuteDeleteAsync(token);
        await db.WaitingFors.Where(item => item.UserId == userId).ExecuteDeleteAsync(token);
        await db.Projects.Where(project => project.UserId == userId).ExecuteDeleteAsync(token);
    }
}
