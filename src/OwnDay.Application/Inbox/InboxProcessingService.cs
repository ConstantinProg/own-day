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

        if (command.ProjectId <= 0)
        {
            return new(ProcessInboxItemStatus.InvalidProject);
        }

        // Only domain construction errors are input errors. Storage failures propagate.
        object target;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            target = command.TargetKind switch
            {
                InboxTargetKind.Action => Action.Create(userId, command.Text, now, command.ProjectId),
                InboxTargetKind.Project => Project.Create(userId, command.Text, now),
                InboxTargetKind.SomedayMaybe => SomedayMaybe.Create(userId, command.Text, now, command.ProjectId),
                InboxTargetKind.Reference => Reference.Create(userId, command.Text, now, command.ProjectId),
                InboxTargetKind.WaitingFor => WaitingFor.Create(userId, command.Text, command.Source,
                    command.WaitingSince ?? now, command.ProjectId),
                _ => throw new InvalidOperationException("Target kind was already validated.")
            };
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

        var targetId = target switch
        {
            Action action => (await actions.AddAsync(action, cancellationToken)).Id,
            Project project => (await structuredItems.AddAsync(project, cancellationToken)).Id,
            SomedayMaybe idea => (await structuredItems.AddAsync(idea, cancellationToken)).Id,
            Reference reference => (await structuredItems.AddAsync(reference, cancellationToken)).Id,
            WaitingFor waiting => (await structuredItems.AddAsync(waiting, cancellationToken)).Id,
            _ => throw new InvalidOperationException("Unsupported inbox target.")
        };

        item.Process(command.TargetKind, targetId, timeProvider.GetUtcNow().UtcDateTime);
        return new(ProcessInboxItemStatus.Processed, command.TargetKind, targetId);
    }
}
