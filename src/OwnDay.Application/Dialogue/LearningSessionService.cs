using OwnDay.Domain;
using OwnDay.Domain.Learning;

namespace OwnDay.Application.Dialogue;

public interface ILearningSessionStore
{
    Task<LearningSession?> FindLatestAsync(UserId user, CancellationToken token);
    void Add(LearningSession session);
}

public sealed class LearningSessionService(ILearningSessionStore store)
{
    // The durable worker serializes these transitions with the user's other Learning changes.
    public async Task<bool> RecordActivityAsync(DialogueTurn turn, Guid programId, Lesson? lesson, CancellationToken token)
    {
        var session = await store.FindLatestAsync(turn.UserId, token);
        var at = turn.ReceivedAt;
        if (session?.IsIdle(at) == true) session.End(at, LearningSessionEnd.Idle, lesson);
        if (session is { EndedAt: null } && session.ProgramId != programId)
            session.End(at < session.LastActivityAt ? session.LastActivityAt : at, LearningSessionEnd.ProgramChanged);
        if (session is { EndedAt: null })
        {
            session.RecordActivity(at, lesson);
            return false;
        }
        store.Add(LearningSession.Start(turn.UserId, programId, at, turn.SourceOrder, lesson));
        return session?.ProgramId == programId && (lesson is null || lesson.ProgramId == programId);
    }

    public async Task EndAsync(DialogueTurn turn, Guid? programId, Lesson? lesson, CancellationToken token)
    {
        var session = await store.FindLatestAsync(turn.UserId, token);
        if (session is not { EndedAt: null })
        {
            if (session?.ProgramId == programId || programId is not { } selected || lesson?.ProgramId != selected) return;
            var closed = LearningSession.Start(turn.UserId, selected, turn.ReceivedAt, turn.SourceOrder, lesson);
            closed.End(turn.ReceivedAt, LearningSessionEnd.Explicit);
            store.Add(closed);
            return;
        }
        var at = turn.ReceivedAt < session.LastActivityAt ? session.LastActivityAt : turn.ReceivedAt;
        if (session.IsIdle(at))
        {
            session.End(at, LearningSessionEnd.Idle, lesson);
            return;
        }
        session.RecordActivity(at, lesson);
        session.End(at, LearningSessionEnd.Explicit);
    }
}
