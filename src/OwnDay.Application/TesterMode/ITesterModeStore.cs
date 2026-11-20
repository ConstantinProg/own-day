using OwnDay.Domain;

namespace OwnDay.Application.TesterMode;

public interface ITesterModeStore
{
    Task<TesterModeState?> FindAsync(UserId userId, CancellationToken cancellationToken);
    void Add(TesterModeState state);
}
