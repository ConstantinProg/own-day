using OwnDay.Application.Inbox.Analysis;

namespace OwnDay.Application.Inbox.Observability;

public interface IOpenAIRequestLedger
{
    Task<Guid?> AdmitAsync(InboxAnalysisRequest request, InboxAnalyzerDescriptor descriptor, CancellationToken cancellationToken);
    Task FinishAsync(Guid id, string code, InboxAnalyzerResponse? response, CancellationToken cancellationToken);
}

public sealed class CostControlledInboxAnalyzer(IInboxAnalyzer inner, IOpenAIRequestLedger ledger) : IInboxAnalyzer
{
    public InboxAnalyzerDescriptor Descriptor => inner.Descriptor;

    public async Task<InboxAnalyzerResponse> AnalyzeAsync(InboxAnalysisRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        inner.ValidateRequest(request);
        var id = await ledger.AdmitAsync(request, Descriptor, cancellationToken);
        if (id is null)
        {
            throw new InboxAnalyzerException(InboxAnalysisCode.BudgetLimit);
        }

        InboxAnalyzerResponse response;
        try
        {
            response = await inner.AnalyzeAsync(request, cancellationToken);
        }
        catch (InboxAnalyzerException exception)
        {
            await ledger.FinishAsync(id.Value, exception.Code.ToStorageValue(), exception.Response, cancellationToken);
            throw;
        }
        // Cancellation/crash/unexpected failure keeps the committed reservation; usage is unknown.
        await ledger.FinishAsync(id.Value, "response", response, cancellationToken);
        return response;
    }
}
