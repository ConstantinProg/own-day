using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.OpenAIAnalysis;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Analysis;

public sealed class OpenAIInboxAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_SdkRequest_UsesStrictSchemaBoundedContextAndCanonicalOutput()
    {
        var proposalJson = InboxAnalysisValidatorTests.Json(
            InboxAnalysisValidatorTests.Proposal(InboxTargetKind.Action, "Купить молоко"));
        using var handler = new RecordingHandler(Response(proposalJson));
        using var http = new HttpClient(handler);
        var analyzer = Analyzer(http);
        using var cancellation = new CancellationTokenSource();

        var response = await analyzer.AnalyzeAsync(Request(), cancellation.Token);

        Assert.Equal(proposalJson, response.Json);
        Assert.Equal("gpt-4.1-mini-test", response.Model);
        Assert.Equal("chatcmpl-test", response.RequestId);
        Assert.Equal(100, response.InputTokens);
        Assert.Equal(25, response.OutputTokens);
        Assert.Equal(1, handler.Calls);
        using var sent = JsonDocument.Parse(handler.Body!);
        var root = sent.RootElement;
        Assert.Equal("gpt-4.1-mini", root.GetProperty("model").GetString());
        Assert.Equal("json_schema", root.GetProperty("response_format").GetProperty("type").GetString());
        var format = root.GetProperty("response_format").GetProperty("json_schema");
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        var messages = root.GetProperty("messages");
        Assert.Contains("preserving meaning", messages[0].GetProperty("content").GetString());
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("купит молоко", input.RootElement.GetProperty("originalText").GetString());
        Assert.Equal(JsonValueKind.Null, input.RootElement.GetProperty("context").GetProperty("timeZoneId").ValueKind);
        Assert.NotNull(new InboxAnalysisValidator().ParseAndValidate(response.Json, Request()));
    }

    [Theory]
    [InlineData("stop", "refused", "refused")]
    [InlineData("length", null, "incomplete_response")]
    [InlineData("content_filter", null, "incomplete_response")]
    public async Task AnalyzeAsync_RefusalOrIncompleteOutput_RejectsBeforeParsing(string finish, string? refusal, string code)
    {
        using var handler = new RecordingHandler(Response("partial", finish, refusal));
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InboxAnalyzerException>(() =>
            Analyzer(http).AnalyzeAsync(Request(), CancellationToken.None));
        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public async Task AnalyzeAsync_ProviderError_DoesNotExposeResponseBodyOrRetry()
    {
        using var handler = new RecordingHandler("{\"error\":{\"message\":\"private text and secret\"}}", HttpStatusCode.TooManyRequests);
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InboxAnalyzerException>(() =>
            Analyzer(http).AnalyzeAsync(Request(), CancellationToken.None));
        Assert.Equal("rate_limit", exception.Code);
        Assert.DoesNotContain("private", exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AnalyzeAsync_ExcessiveContext_RejectsBeforeNetwork()
    {
        using var handler = new RecordingHandler("{}");
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InboxAnalyzerException>(() =>
            Analyzer(http).AnalyzeAsync(Request() with { OriginalText = new string('x', 48_001) }, CancellationToken.None));
        Assert.Equal("context_limit", exception.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(401, null, "configuration")]
    [InlineData(403, null, "configuration")]
    [InlineData(400, null, "permanent_failure")]
    [InlineData(404, null, "permanent_failure")]
    [InlineData(429, "insufficient_quota", "budget_limit")]
    [InlineData(429, "billing_hard_limit_reached", "budget_limit")]
    [InlineData(429, "rate_limit_exceeded", "rate_limit")]
    [InlineData(503, null, "provider_failure")]
    [InlineData(0, null, "provider_failure")]
    public async Task AnalyzeAsync_ProviderStatus_ClassifiesWithoutLeakingDiagnostics(int status, string? code, string expected)
    {
        var body = JsonSerializer.Serialize(new { error = new { code, message = "private user text and key" } });
        using var handler = new RecordingHandler(body, (HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InboxAnalyzerException>(() => Analyzer(http).AnalyzeAsync(Request(), CancellationToken.None));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task AnalyzeAsync_MalformedRateLimitBody_UsesSafeRetryableCode(string body)
    {
        using var handler = new RecordingHandler(body, HttpStatusCode.TooManyRequests);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InboxAnalyzerException>(() => Analyzer(http).AnalyzeAsync(Request(), CancellationToken.None));
        Assert.Equal("rate_limit", error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AnalyzeAsync_Cancellation_PropagatesToHttpTransport()
    {
        using var handler = new CancellationHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = Analyzer(http).AnalyzeAsync(Request(), cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(handler.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false, null, 4096, 60, true)]
    [InlineData(true, null, 4096, 60, true)]
    [InlineData(true, "dummy-key", 4096, 60, true)]
    [InlineData(true, "dummy-key", 0, 60, false)]
    [InlineData(true, "dummy-key", 4096, 0, false)]
    public void Options_EnabledAndLimits_ValidateWithoutRealKey(bool enabled, string? key, int tokens, int timeout, bool valid)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InboxAnalysis:OpenAI:Enabled"] = enabled.ToString(),
            ["InboxAnalysis:OpenAI:ApiKey"] = key,
            ["InboxAnalysis:OpenAI:MaxOutputTokens"] = tokens.ToString(),
            ["InboxAnalysis:OpenAI:TimeoutSeconds"] = timeout.ToString()
        }).Build());
        services.AddInboxAnalysis();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OpenAIInboxAnalysisOptions>>();
        if (valid)
        {
            Assert.Equal(enabled, options.Value.Enabled);
        }
        else
        {
            Assert.Throws<OptionsValidationException>(() => options.Value);
        }
    }

    private static OpenAIInboxAnalyzer Analyzer(HttpClient http) => new(
        new ChatClient("gpt-4.1-mini", new ApiKeyCredential("dummy-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        }), Options.Create(new OpenAIInboxAnalysisOptions()));

    private static InboxAnalysisRequest Request() => new("купит молоко",
        new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc), new(null, [], false), InboxAnalysisService.SupportedTypes);

    private static string Response(string content, string finish = "stop", string? refusal = null) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-test", @object = "chat.completion", created = 1790200000, model = "gpt-4.1-mini-test",
        choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content, refusal } } },
        usage = new { prompt_tokens = 100, completion_tokens = 25, total_tokens = 125 }
    });

    private sealed class RecordingHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
