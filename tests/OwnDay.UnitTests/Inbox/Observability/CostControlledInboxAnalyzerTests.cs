using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Observability;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Observability;

public sealed class CostControlledInboxAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_LocalValidationRejects_DoesNotReserveOrCallProvider()
    {
        var ledger = new RecordingLedger();
        var inner = new RejectingAnalyzer();
        var controlled = new CostControlledInboxAnalyzer(inner, ledger);
        var error = await Assert.ThrowsAsync<InboxAnalyzerException>(() => controlled.AnalyzeAsync(
            new("text", DateTime.UtcNow, new(null, [], false), []), CancellationToken.None));
        Assert.Equal(InboxAnalysisCode.ContextLimit, error.Code);
        Assert.Equal(0, ledger.Admissions);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task AnalyzeAsync_AlreadyCancelled_DoesNotReserveBudget()
    {
        var ledger = new RecordingLedger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var controlled = new CostControlledInboxAnalyzer(new RejectingAnalyzer(), ledger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controlled.AnalyzeAsync(
            new("text", DateTime.UtcNow, new(null, [], false), []), cancellation.Token));
        Assert.Equal(0, ledger.Admissions);
    }

    private sealed class RejectingAnalyzer : IInboxAnalyzer
    {
        public int Calls { get; private set; }
        public InboxAnalyzerDescriptor Descriptor => new("test", "inbox-v1", "test");
        public void ValidateRequest(InboxAnalysisRequest request) => throw new InboxAnalyzerException(InboxAnalysisCode.ContextLimit);
        public Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Validation must precede invocation.");
        }
    }

    private sealed class RecordingLedger : IOpenAIRequestLedger
    {
        public int Admissions { get; private set; }
        public Task<Guid?> AdmitAsync(InboxAnalysisRequest request, InboxAnalyzerDescriptor descriptor, CancellationToken cancellationToken)
        {
            Admissions++;
            return Task.FromResult<Guid?>(Guid.NewGuid());
        }
        public Task FinishAsync(Guid id, string code, InboxAnalyzerResponse? response, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
