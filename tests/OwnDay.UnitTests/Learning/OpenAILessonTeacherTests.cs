using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OwnDay.Application.Learning;
using OwnDay.Infrastructure.OpenAIAnalysis;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class OpenAILessonTeacherTests
{
    [Theory]
    [InlineData("stop", null, "not-json")]
    [InlineData("length", null, "{}")]
    [InlineData("stop", "refused", "{}")]
    public async Task GenerateAsync_InvalidProviderOutput_LogsSafeReason(string finish, string? refusal, string content)
    {
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = finish,
                message = new { role = "assistant", content, refusal } } }
        }));
        using var http = new HttpClient(handler);
        var logger = new RecordingLogger();
        var teacher = Teacher(http, logger);

        var error = await Assert.ThrowsAsync<LessonProviderException>(() => teacher.GenerateAsync("private topic", CancellationToken.None));

        Assert.Equal(LessonProviderFailure.InvalidResponse, error.Reason);
        Assert.Contains(logger.Messages, m => m.Contains("InvalidResponse", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("private topic", StringComparison.Ordinal) || m.Contains("not-json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_HttpFailure_LogsStatusWithoutResponseBody()
    {
        using var handler = new ResponseHandler("private provider error", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var logger = new RecordingLogger();
        var error = await Assert.ThrowsAsync<LessonProviderException>(() => Teacher(http, logger).GenerateAsync("topic", CancellationToken.None));
        Assert.Equal(LessonProviderFailure.HttpError, error.Reason);
        Assert.Contains(logger.Messages, m => m.Contains("401", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("private provider error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_CallerCanceled_DoesNotCreateClient()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var teacher = new OpenAILessonTeacher(() => throw new InvalidOperationException(),
            Options.Create(new OpenAIInboxAnalysisOptions()), new RecordingLogger());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => teacher.GenerateAsync("topic", cancellation.Token));
    }

    [Fact]
    public async Task GenerateAsync_ProviderTimeout_ClassifiesAndLogsFailure()
    {
        var logger = new RecordingLogger();
        var teacher = new OpenAILessonTeacher(() => throw new OperationCanceledException(),
            Options.Create(new OpenAIInboxAnalysisOptions { Enabled = true, ApiKey = "dummy" }), logger);
        var error = await Assert.ThrowsAsync<LessonProviderException>(() => teacher.GenerateAsync("topic", CancellationToken.None));
        Assert.Equal(LessonProviderFailure.Timeout, error.Reason);
        Assert.Contains(logger.Messages, m => m.Contains("Timeout", StringComparison.Ordinal));
    }

    private static OpenAILessonTeacher Teacher(HttpClient http, RecordingLogger logger) => new(
        () => new ChatClient("test", new ApiKeyCredential("dummy-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        }), Options.Create(new OpenAIInboxAnalysisOptions { Enabled = true, ApiKey = "dummy-key" }), logger);

    private sealed class ResponseHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class RecordingLogger : ILogger<OpenAILessonTeacher>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
