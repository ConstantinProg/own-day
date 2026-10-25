using OwnDay.Application.Inbox.Analysis;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Analysis;

public sealed class InboxAnalysisProcessingOptionsTests
{
    [Theory]
    [InlineData("refused")]
    [InlineData("invalid_response")]
    [InlineData("incomplete_response")]
    [InlineData("context_limit")]
    [InlineData("input_limit")]
    [InlineData("configuration")]
    [InlineData("budget_limit")]
    [InlineData("permanent_failure")]
    [InlineData("item_inactive")]
    public void RetryAfter_TerminalReason_DoesNotRetry(string code)
    {
        Assert.Null(new InboxAnalysisProcessingOptions().RetryAfter(code, 1, 0.5));
    }

    [Theory]
    [InlineData("provider_failure")]
    [InlineData("rate_limit")]
    [InlineData("timeout")]
    public void RetryAfter_TransientReason_UsesExponentialBoundedJitterAndStopsAtLimit(string code)
    {
        var options = new InboxAnalysisProcessingOptions { MaxAttempts = 5, InitialBackoffSeconds = 30, MaxBackoffSeconds = 100 };
        Assert.Equal(TimeSpan.FromSeconds(15), options.RetryAfter(code, 1, 0));
        Assert.Equal(TimeSpan.FromSeconds(30), options.RetryAfter(code, 1, 1));
        Assert.Equal(TimeSpan.FromSeconds(60), options.RetryAfter(code, 2, 1));
        Assert.Equal(TimeSpan.FromSeconds(100), options.RetryAfter(code, 3, 1));
        Assert.Equal(TimeSpan.FromSeconds(50), options.RetryAfter(code, 4, 0));
        Assert.Null(options.RetryAfter(code, 5, 1));
        Assert.Null(options.RetryAfter(code, 6, 1));
    }
}
