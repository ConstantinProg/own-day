using OwnDay.Application.Inbox;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using Xunit;

namespace OwnDay.UnitTests.Inbox;

public sealed class InboxCaptureServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CaptureAsync_ValidInput_AddsOneActiveInboxItemOnly()
    {
        var store = new RecordingStore();
        var service = new InboxCaptureService(store, new FixedTimeProvider(Now));
        using var source = new CancellationTokenSource();
        var text = "  do something?\n" + new string('x', 500);

        var result = await service.CaptureAsync(new UserId(3), text, source.Token);

        Assert.Same(result, Assert.Single(store.Items));
        Assert.Equal(new UserId(3), result.UserId);
        Assert.Equal(text, result.OriginalText);
        Assert.Equal(Now, result.CapturedAt);
        Assert.Equal(InboxItemStatus.Active, result.Status);
        Assert.Null(result.TargetKind);
        Assert.Equal(source.Token, store.LastToken);
    }

    [Fact]
    public async Task CaptureAsync_InvalidInput_DoesNotWrite()
    {
        var store = new RecordingStore();
        var service = new InboxCaptureService(store, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CaptureAsync(default, "text", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CaptureAsync(new UserId(1), " \t", CancellationToken.None));
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Read_AnotherOwnerOrMissingItem_ReturnsNoData()
    {
        var store = new RecordingStore();
        var service = new InboxCaptureService(store, new FixedTimeProvider(Now));
        await service.CaptureAsync(new UserId(1), "secret", CancellationToken.None);

        Assert.Empty(await service.GetActiveAsync(new UserId(2), CancellationToken.None));
        Assert.Null(await service.FindAsync(new UserId(2), 1, CancellationToken.None));
        Assert.Null(await service.FindAsync(new UserId(1), 99, CancellationToken.None));
    }

    private sealed class RecordingStore : IInboxItemStore
    {
        public List<InboxItem> Items { get; } = [];
        public CancellationToken LastToken { get; private set; }

        public Task<InboxItem> AddAsync(InboxItem item, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            Items.Add(item);
            return Task.FromResult(item);
        }

        public Task<IReadOnlyList<InboxItem>> GetActiveAsync(UserId userId, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<InboxItem>>(Items.Where(item => item.UserId == userId && item.Status == InboxItemStatus.Active).ToList());
        }

        public Task<InboxItem?> FindAsync(UserId userId, long itemId, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return Task.FromResult(Items.FirstOrDefault(item => item.UserId == userId && item.Id == itemId));
        }
    }
}
