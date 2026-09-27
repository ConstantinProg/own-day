using OwnDay.Application.Actions;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox;

public sealed class InboxProcessingService(
    IInboxProcessingStore store,
    ActionService actions,
    StructuredItemService structuredItems,
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
        CancellationToken cancellationToken)
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

        long targetId;
        try
        {
            targetId = command.TargetKind switch
            {
                InboxTargetKind.Action => (await actions.AddAsync(userId, command.Text, command.ProjectId, cancellationToken)).Id,
                InboxTargetKind.Project => (await structuredItems.CreateProjectAsync(userId, command.Text, cancellationToken)).Id,
                InboxTargetKind.SomedayMaybe => (await structuredItems.CreateSomedayMaybeAsync(userId, command.Text, command.ProjectId, cancellationToken)).Id,
                InboxTargetKind.Reference => (await structuredItems.CreateReferenceAsync(userId, command.Text, command.ProjectId, cancellationToken)).Id,
                InboxTargetKind.WaitingFor => (await structuredItems.CreateWaitingForAsync(
                    userId, command.Text, command.Source, command.WaitingSince, command.ProjectId, cancellationToken)).Id,
                _ => throw new InvalidOperationException("Target kind was already validated.")
            };
        }
        catch (ArgumentException exception) when (exception.ParamName == "projectId")
        {
            return new(ProcessInboxItemStatus.InvalidProject);
        }
        catch (ArgumentException)
        {
            return new(ProcessInboxItemStatus.InvalidTargetData);
        }

        item.Process(command.TargetKind, targetId, timeProvider.GetUtcNow().UtcDateTime);
        return new(ProcessInboxItemStatus.Processed, command.TargetKind, targetId);
    }
}
