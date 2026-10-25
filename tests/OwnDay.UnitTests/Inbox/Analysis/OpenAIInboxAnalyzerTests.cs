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
        Assert.Equal("gpt-6-luna-test", response.Model);
        Assert.Equal("chatcmpl-test", response.RequestId);
        Assert.Equal(100, response.InputTokens);
        Assert.Equal(25, response.OutputTokens);
        Assert.Equal(64, response.CachedInputTokens);
        Assert.Equal(1, handler.Calls);
        using var sent = JsonDocument.Parse(handler.Body!);
        var root = sent.RootElement;
        Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(8192, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("top_p", out _));
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.Equal(new("gpt-6-luna", "inbox-v1", "inbox-analysis-v3", "low", 8192, 60), analyzer.Descriptor);
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
    [InlineData("inbox-analysis-v1")]
    [InlineData("inbox-analysis-v2")]
    [InlineData("inbox-analysis-v3")]
    public async Task AnalyzeAsync_HistoricalPrompt_UsesExactVersionInWireRequestAndDescriptor(string version)
    {
        using var handler = new RecordingHandler(Response("{}"));
        using var http = new HttpClient(handler);
        var analyzer = Analyzer(http, promptVersion: version);

        await analyzer.AnalyzeAsync(Request(), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(version, analyzer.Descriptor.PromptVersion);
        Assert.Equal(OpenAIInboxAnalysisFormat.GetPrompt(version), sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("inbox-v1", analyzer.Descriptor.SchemaVersion);
    }

    [Fact]
    public void GetPrompt_UnknownVersion_RejectsWithoutFallback() =>
        Assert.Throws<ArgumentException>(() => OpenAIInboxAnalysisFormat.GetPrompt("inbox-analysis-unknown"));

    [Theory]
    [InlineData("gpt-6-luna", "none", "none")]
    [InlineData("gpt-6-luna", "medium", "medium")]
    [InlineData("gpt-6-luna", "high", "high")]
    [InlineData("gpt-6-luna", "xhigh", "xhigh")]
    [InlineData("gpt-6-luna", "max", "max")]
    [InlineData("gpt-4.1-mini", "low", null)]
    [InlineData("custom-model", "low", null)]
    public async Task AnalyzeAsync_ConfiguredModelAndEffort_SendsOnlySupportedReasoning(
        string model, string effort, string? expected)
    {
        using var handler = new RecordingHandler(Response("{}"));
        using var http = new HttpClient(handler);
        var analyzer = Analyzer(http, new() { Model = model, ReasoningEffort = effort, MaxOutputTokens = 4096 });

        await analyzer.AnalyzeAsync(Request(), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(model, sent.RootElement.GetProperty("model").GetString());
        Assert.Equal(4096, sent.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(expected, analyzer.Descriptor.ReasoningEffort);
        Assert.Equal(expected is not null, sent.RootElement.TryGetProperty("reasoning_effort", out var reasoning));
        if (expected is not null)
        {
            Assert.Equal(expected, reasoning.GetString());
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"items\":[],\"unexpected\":true}")]
    public async Task AnalyzeAsync_InvalidStructuredContent_StillRequiresIndependentValidation(string json)
    {
        using var handler = new RecordingHandler(Response(json));
        using var http = new HttpClient(handler);
        var response = await Analyzer(http).AnalyzeAsync(Request(), CancellationToken.None);
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(response.Json, Request()));
    }

    [Theory]
    [InlineData("low", true)]
    [InlineData("max", true)]
    [InlineData("ultra", false)]
    [InlineData("LOW", false)]
    [InlineData("", false)]
    public void Options_ReasoningEffort_ValidatesOnResolution(string effort, bool valid)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InboxAnalysis:OpenAI:ReasoningEffort"] = effort
        }).Build());
        services.AddInboxAnalysis();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OpenAIInboxAnalysisOptions>>();
        if (valid)
        {
            Assert.Equal(effort, options.Value.ReasoningEffort);
        }
        else
        {
            Assert.Throws<OptionsValidationException>(() => options.Value);
        }
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
        Assert.Equal(code, exception.Code.ToStorageValue());
    }

    [Fact]
    public async Task AnalyzeAsync_ProviderError_DoesNotExposeResponseBodyOrRetry()
    {
        using var handler = new RecordingHandler("{\"error\":{\"message\":\"private text and secret\"}}", HttpStatusCode.TooManyRequests);
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<InboxAnalyzerException>(() =>
            Analyzer(http).AnalyzeAsync(Request(), CancellationToken.None));
        Assert.Equal("rate_limit", exception.Code.ToStorageValue());
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
        Assert.Equal("context_limit", exception.Code.ToStorageValue());
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
        Assert.Equal(expected, error.Code.ToStorageValue());
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
        Assert.Equal("rate_limit", error.Code.ToStorageValue());
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

    private static OpenAIInboxAnalyzer Analyzer(HttpClient http, OpenAIInboxAnalysisOptions? settings = null, string? promptVersion = null) => new(
        new ChatClient((settings ?? new()).Model, new ApiKeyCredential("dummy-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        }), Options.Create(settings ?? new()), promptVersion);

    private static InboxAnalysisRequest Request() => new("купит молоко",
        new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc), new(null, [], false), InboxAnalysisService.SupportedTypes);

    private static string Response(string content, string finish = "stop", string? refusal = null) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-test", @object = "chat.completion", created = 1790200000, model = "gpt-6-luna-test",
        choices = new[] { new { index = 0, finish_reason = finish, message = new { role = "assistant", content, refusal } } },
        usage = new { prompt_tokens = 100, completion_tokens = 25, total_tokens = 125,
            prompt_tokens_details = new { cached_tokens = 64 },
            completion_tokens_details = new { reasoning_tokens = 20 } }
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
