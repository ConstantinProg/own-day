using OwnDay.Application.Inbox.Analysis;

namespace OwnDay.Infrastructure.Learning;

// One detached invocation scope; usage survives provider validation failures and
// is reconciled independently of the request's lease or Domain transaction.
public sealed class LearningProviderUsage
{
    public Guid? LedgerAttemptId { get; set; }
    public InboxAnalyzerResponse? Response { get; set; }
}
