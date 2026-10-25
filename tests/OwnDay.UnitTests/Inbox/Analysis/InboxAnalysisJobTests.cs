using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Analysis;

public sealed class InboxAnalysisJobTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Finish_TransientFailure_KeepsAnalysisPendingAndReleasesLease()
    {
        using var db = Context();
        var (job, item, analysis) = RunningJob(db);

        var result = job.Finish(item, analysis, InboxAnalysisOutcome.Failure(InboxAnalysisCode.Timeout),
            TimeSpan.FromSeconds(30), Now);

        Assert.Equal(InboxAnalysisJobStatus.Ready, result.Status);
        Assert.Equal(InboxAnalysisCode.Timeout, result.Code);
        Assert.Equal(Now.AddSeconds(30), job.AvailableAt);
        Assert.Equal(InboxAnalysisStatus.Pending, analysis.Status);
        Assert.Null(job.LeaseToken);
        Assert.Null(job.LeaseUntil);
    }

    [Fact]
    public void Finish_ItemDiscardedDuringAnalysis_SkipsInsteadOfRetrying()
    {
        using var db = Context();
        var (job, item, analysis) = RunningJob(db);
        item.Discard(Now);

        var result = job.Finish(item, analysis, InboxAnalysisOutcome.Failure(InboxAnalysisCode.Timeout),
            TimeSpan.FromSeconds(30), Now);

        Assert.Equal(InboxAnalysisJobStatus.Skipped, result.Status);
        Assert.Equal(InboxAnalysisCode.ItemInactive, result.Code);
        Assert.Equal(InboxAnalysisStatus.Failed, analysis.Status);
        Assert.Equal("{\"code\":\"item_inactive\"}", analysis.DiagnosticsJson);
        Assert.Equal("item_inactive", job.LastCode);
    }

    [Fact]
    public void Finish_InvalidResponse_CompletesAnalysisWithInvalidResponseStatus()
    {
        using var db = Context();
        var (job, item, analysis) = RunningJob(db);

        var result = job.Finish(item, analysis, InboxAnalysisOutcome.Failure(InboxAnalysisCode.InvalidResponse), null, Now);

        Assert.Equal(InboxAnalysisJobStatus.Failed, result.Status);
        Assert.Equal(InboxAnalysisStatus.InvalidResponse, analysis.Status);
        Assert.Equal(Now, analysis.CompletedAt);
    }

    [Fact]
    public void Finish_AnalysisAlreadyFinished_AcknowledgesWithoutOverwritingResult()
    {
        using var db = Context();
        var (job, item, analysis) = RunningJob(db);
        analysis.Finish(InboxAnalysisStatus.Failed, Now, "{\"code\":\"configuration\"}");
        item.Discard(Now);

        var result = job.Finish(item, analysis, InboxAnalysisOutcome.Failure(InboxAnalysisCode.ItemInactive), null, Now);

        Assert.Equal(InboxAnalysisJobStatus.Failed, result.Status);
        Assert.Equal(InboxAnalysisCode.AlreadyFinished, result.Code);
        Assert.Equal("{\"code\":\"configuration\"}", analysis.DiagnosticsJson);
    }

    [Fact]
    public void Finish_LeaseExpired_RejectsWithoutChangingAnalysis()
    {
        using var db = Context();
        var (job, item, analysis) = RunningJob(db);

        Assert.Throws<InvalidOperationException>(() => job.Finish(item, analysis,
            InboxAnalysisOutcome.Failure(InboxAnalysisCode.Timeout), null, Now.AddMinutes(2)));
        Assert.Equal(InboxAnalysisJobStatus.Running, job.Status);
        Assert.Equal(InboxAnalysisStatus.Pending, analysis.Status);
    }

    private static OwnDayDbContext Context() => new(new DbContextOptionsBuilder<OwnDayDbContext>()
        .UseSqlite("Data Source=:memory:").Options);

    private static (InboxAnalysisJob Job, InboxItem Item, InboxAnalysis Analysis) RunningJob(OwnDayDbContext db)
    {
        var item = InboxItem.Capture(new UserId(11), "Original", Now);
        var job = InboxAnalysisJob.Schedule(item);
        // Hydrate the reservation produced by the store's atomic SQL claim. No database I/O is needed.
        db.Entry(job).Property(row => row.Status).CurrentValue = InboxAnalysisJobStatus.Running;
        db.Entry(job).Property(row => row.LeaseToken).CurrentValue = Guid.NewGuid();
        db.Entry(job).Property(row => row.LeaseUntil).CurrentValue = Now.AddMinutes(2);
        var analysis = InboxAnalysis.Start(item.UserId, item.Id, 1, new("fake", "v1", "v1"), "{}", "hash", "hash", Now);
        job.AttachAnalysis(analysis);
        return (job, item, analysis);
    }
}
