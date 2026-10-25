using OwnDay.Application.Actions;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain.Inbox;
using OwnDay.Domain.Projects;
using OwnDay.Domain.References;
using OwnDay.Domain.SomedayMaybes;
using OwnDay.Domain.WaitingFors;
using OwnDay.Domain;
using Action = OwnDay.Domain.Actions.Action;

namespace OwnDay.Application.Inbox;

public sealed class InboxProcessingService(
    IInboxProcessingStore store,
    IActionStore actions,
    IStructuredItemStore structuredItems,
    TimeProvider timeProvider)
{
    public Task<ProcessInboxItemResult> ProcessAsync(
        UserId userId,
        ProcessInboxItem command,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        ArgumentNullException.ThrowIfNull(command);
        if (command.InboxItemId <= 0)
        {
            return Task.FromResult(new ProcessInboxItemResult(ProcessInboxItemStatus.NotFound));
        }

        return store.ExecuteLockedAsync(userId, command.InboxItemId,
            (item, token) => ProcessLockedAsync(userId, item, command, token), cancellationToken);
    }

    public Task<DiscardInboxItemResult> DiscardAsync(
        UserId userId,
        long itemId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId.Value);
        if (itemId <= 0)
        {
            return Task.FromResult(DiscardInboxItemResult.NotFound);
        }

        return store.ExecuteLockedAsync(userId, itemId, (item, _) =>
        {
            if (item is null)
            {
                return Task.FromResult(DiscardInboxItemResult.NotFound);
            }

            if (item.Status == InboxItemStatus.Processed)
            {
                return Task.FromResult(DiscardInboxItemResult.AlreadyProcessed);
            }

            if (item.Status == InboxItemStatus.Discarded)
            {
                return Task.FromResult(DiscardInboxItemResult.AlreadyDiscarded);
            }

            item.Discard(timeProvider.GetUtcNow().UtcDateTime);
            return Task.FromResult(DiscardInboxItemResult.Discarded);
        }, cancellationToken);
    }

    private async Task<ProcessInboxItemResult> ProcessLockedAsync(
        UserId userId,
        InboxItem? item,
        ProcessInboxItem command,
        CancellationToken cancellationToken,
        bool finalize = true)
    {
        if (item is null)
        {
            return new(ProcessInboxItemStatus.NotFound);
        }

        if (item.Status == InboxItemStatus.Processed)
        {
            return new(ProcessInboxItemStatus.AlreadyProcessed, item.TargetKind, item.TargetId);
        }

        if (item.Status == InboxItemStatus.Discarded)
        {
            return new(ProcessInboxItemStatus.Discarded);
        }

        if (!Enum.IsDefined(command.TargetKind) ||
            (command.TargetKind == InboxTargetKind.Project && command.ProjectId is not null) ||
            (command.TargetKind != InboxTargetKind.WaitingFor && (command.Source is not null || command.WaitingSince is not null)))
        {
            return new(ProcessInboxItemStatus.InvalidTargetData);
        }

        if (command.ProjectId <= 0)
        {
            return new(ProcessInboxItemStatus.InvalidProject);
        }

        // Only domain construction errors are input errors. Storage failures propagate.
        Func<CancellationToken, Task<long>> saveTarget;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            switch (command.TargetKind)
            {
                case InboxTargetKind.Action:
                    var action = Action.Create(userId, command.Text, now, command.ProjectId);
                    saveTarget = async token => (await actions.AddAsync(action, token)).Id;
                    break;
                case InboxTargetKind.Project:
                    var project = Project.Create(userId, command.Text, now);
                    saveTarget = async token => (await structuredItems.AddAsync(project, token)).Id;
                    break;
                case InboxTargetKind.SomedayMaybe:
                    var idea = SomedayMaybe.Create(userId, command.Text, now, command.ProjectId);
                    saveTarget = async token => (await structuredItems.AddAsync(idea, token)).Id;
                    break;
                case InboxTargetKind.Reference:
                    var reference = Reference.Create(userId, command.Text, now, command.ProjectId);
                    saveTarget = async token => (await structuredItems.AddAsync(reference, token)).Id;
                    break;
                case InboxTargetKind.WaitingFor:
                    var waiting = WaitingFor.Create(userId, command.Text, command.Source, command.WaitingSince ?? now, command.ProjectId);
                    saveTarget = async token => (await structuredItems.AddAsync(waiting, token)).Id;
                    break;
                default:
                    throw new InvalidOperationException("Target kind was already validated.");
            }
        }
        catch (ArgumentException)
        {
            return new(ProcessInboxItemStatus.InvalidTargetData);
        }

        if (command.ProjectId is long projectId &&
            !await structuredItems.ProjectBelongsToAsync(projectId, userId, cancellationToken))
        {
            return new(ProcessInboxItemStatus.InvalidProject);
        }

        var targetId = await saveTarget(cancellationToken);

        if (finalize)
        {
            item.Process(command.TargetKind, targetId, timeProvider.GetUtcNow().UtcDateTime);
        }
        return new(ProcessInboxItemStatus.Processed, command.TargetKind, targetId);
    }

    // The caller owns the existing Inbox lock and transaction. A failure must escape to roll back every target.
    internal async Task<IReadOnlyList<InboxTarget>> ProcessBatchLockedAsync(UserId userId, InboxItem item,
        IReadOnlyList<ProcessInboxItem> commands, CancellationToken cancellationToken)
    {
        var targets = new List<InboxTarget>();
        foreach (var command in commands)
        {
            var result = await ProcessLockedAsync(userId, item, command, cancellationToken, finalize: false);
            if (result.Status != ProcessInboxItemStatus.Processed)
            {
                throw new InvalidOperationException("Validated automatic target could not be created.");
            }
            targets.Add(new(command.TargetKind, result.TargetId!.Value));
        }
        if (!item.ProcessMany(targets, timeProvider.GetUtcNow().UtcDateTime))
        {
            throw new InvalidOperationException("Inbox item is no longer active.");
        }
        return targets.AsReadOnly();
    }
}
