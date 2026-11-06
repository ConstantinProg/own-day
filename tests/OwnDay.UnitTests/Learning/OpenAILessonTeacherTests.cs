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
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.OpenAIAnalysis;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class OpenAILessonTeacherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateAsync_SourceContentAndModelQuote_RecordsOnlyServerVerifiedProvenance(bool available)
    {
        const string quote = "Literal names MUST be lowercase.";
        using var sourceHttp = new HttpClient(new ResponseHandler(available ? "RFC 8259\n" + quote : "unavailable",
            available ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, "text/plain"));
        using var reader = new RfcLessonSourceReader(sourceHttp, TimeProvider.System);
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "actual-model", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant",
                content = JsonSerializer.Serialize(new { Theory = quote, Question = "Which case?", ExpectedAnswer = "lowercase", SourceQuote = quote,
                    Provenance = new { Status = "verified", Reference = "http://127.0.0.1/private" } }) } } }
        }));
        using var http = new HttpClient(handler);
        var teacher = new OpenAILessonTeacher(() => new ChatClient("test", new ApiKeyCredential("dummy-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        }), Options.Create(new OpenAIInboxAnalysisOptions { Enabled = true, ApiKey = "dummy-key" }), new RecordingLogger(), reader);
        var result = await teacher.GenerateAsync(new("JSON"), CancellationToken.None);
        Assert.Equal("lowercase", result.ExpectedAnswer);
        Assert.NotNull(result.Provenance);
        Assert.Equal("OpenAI:actual-model", result.Provenance.Generator);
        Assert.Equal(RfcLessonSourceReader.Reference, result.Provenance.Reference);
        Assert.Equal(available ? MaterialSourceStatus.QuoteMatched : MaterialSourceStatus.Unavailable, result.Provenance.Status);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("never instructions", messages[0].GetProperty("content").GetString());
        Assert.Contains("never invent", messages[0].GetProperty("content").GetString(), StringComparison.OrdinalIgnoreCase);
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(available, input.RootElement.GetProperty("Source").GetProperty("Text").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task GenerateAsync_ConfirmedSharedSkill_SendsDescriptionAndProvisionalStateInProgramContext()
    {
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop",
                message = new { role = "assistant", content = "{\"Theory\":\"Context\",\"Question\":\"Application\"}" } } }
        }));
        using var http = new HttpClient(handler);
        await Teacher(http, new RecordingLogger()).GenerateAsync(new("Mechanics", new("Physics", "Goal", "Beginner", null, null),
            SharedSkill: new("Algebraic concept", new(SkillMastery.Developing, 1, 1, 0))), CancellationToken.None);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("explicitly confirmed", messages[0].GetProperty("content").GetString());
        Assert.Contains("Do not assume the new topic is complete", messages[0].GetProperty("content").GetString());
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("Physics", input.RootElement.GetProperty("Program").GetProperty("Discipline").GetString());
        var shared = input.RootElement.GetProperty("SharedSkill");
        Assert.Equal("Algebraic concept", shared.GetProperty("Description").GetString());
        Assert.Equal(1, shared.GetProperty("State").GetProperty("PositiveAssessments").GetInt32());
    }

    [Fact]
    public async Task GenerateAsync_ScheduledReview_RequestsNewApplicationWithoutRevealingAnswer()
    {
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop",
                message = new { role = "assistant", content = "{\"Theory\":\"Context\",\"Question\":\"New application\"}" } } }
        }));
        using var http = new HttpClient(handler);
        await Teacher(http, new RecordingLogger()).GenerateAsync(new("Fractions", IsReview: true), CancellationToken.None);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("new application scenario", messages[0].GetProperty("content").GetString());
        Assert.Contains("without revealing the answer", messages[0].GetProperty("content").GetString());
        Assert.Contains("untrusted data", messages[0].GetProperty("content").GetString());
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.True(input.RootElement.GetProperty("IsReview").GetBoolean());
        Assert.Equal("Fractions", input.RootElement.GetProperty("Topic").GetString());
    }

    [Fact]
    public async Task ProposeAsync_RepeatedDifficulty_SendsBoundedEvidenceAndStrictActionCatalog()
    {
        var proposal = new TeachingProposal("Simplify", "basics", "Try a smaller example.");
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop",
                message = new { role = "assistant", content = JsonSerializer.Serialize(proposal) } } }
        }));
        using var http = new HttpClient(handler);
        var request = new TeachingRequest("Goal", "Mathematics", [new("basics", "Basics", [])], "basics",
            new HashSet<string>(), new(SkillMastery.NeedsPractice, 2, 0, 2), ["Possible difficulty"]);
        var result = await Teacher(http, new RecordingLogger()).ProposeAsync(request, CancellationToken.None);
        Assert.Equal(proposal, result);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("untrusted data", messages[0].GetProperty("content").GetString());
        Assert.Contains("never Advance", messages[0].GetProperty("content").GetString());
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("basics", input.RootElement.GetProperty("CurrentTopicCode").GetString());
        Assert.Equal(2, input.RootElement.GetProperty("State").GetProperty("NegativeAssessments").GetInt32());
        Assert.Equal("Possible difficulty", input.RootElement.GetProperty("RecentFeedback")[0].GetString());
        var format = body.RootElement.GetProperty("response_format").GetProperty("json_schema");
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal(6, format.GetProperty("schema").GetProperty("properties").GetProperty("Action").GetProperty("enum").GetArrayLength());
    }

    [Theory]
    [InlineData("Hint")]
    [InlineData("Explain")]
    [InlineData("Simplify")]
    [InlineData("Practice")]
    [InlineData("ReviewPrerequisite")]
    [InlineData("Advance")]
    public async Task GenerateAsync_AdaptiveAction_PassesTeachingContextWithoutChangingProgramGoal(string action)
    {
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop",
                message = new { role = "assistant", content = "{\"Theory\":\"Example\",\"Question\":\"Question\"}" } } }
        }));
        using var http = new HttpClient(handler);
        await Teacher(http, new RecordingLogger()).GenerateAsync(new("Basics", new("Math", "Goal", "Beginner", null, null),
            new(Enum.Parse<TeachingAction>(action), "Reason", new(SkillMastery.NeedsPractice, 2, 0, 2), ["AI hypothesis"])), CancellationToken.None);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("without revealing the answer", messages[0].GetProperty("content").GetString());
        Assert.Contains("reduces complexity", messages[0].GetProperty("content").GetString());
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("Goal", input.RootElement.GetProperty("Program").GetProperty("Goal").GetString());
        Assert.Equal(action, input.RootElement.GetProperty("Teaching").GetProperty("Action").GetString());
    }

    [Fact]
    public async Task AssessAsync_ProviderModel_PreservesSourceOutsideModelInterpretation()
    {
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "actual-model", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop",
                message = new { role = "assistant", content = "{\"Feedback\":\"Reason\",\"IsCorrect\":true,\"Source\":\"invented source\"}" } } }
        }));
        using var http = new HttpClient(handler);
        var result = await Teacher(http, new RecordingLogger()).AssessAsync(
            Lesson.Start(new UserId(1), "Topic", "Theory", "Question"), "Answer", CancellationToken.None);
        Assert.Equal("OpenAI:actual-model", result.Source);
        Assert.Equal("Reason", result.Feedback);
        Assert.True(result.IsCorrect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("x = 2")]
    public async Task PlanAsync_ExperienceAndOptionalAnswer_SendsStructuredUntrustedContext(string? answer)
    {
        var proposal = new CurriculumProposal("Mathematics", [new("equations", "Equations", [])], answer is null ? "Solve x + 2 = 4" : null);
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop",
                message = new { role = "assistant", content = JsonSerializer.Serialize(proposal) } } }
        }));
        using var http = new HttpClient(handler);
        var result = await Teacher(http, new RecordingLogger()).PlanAsync(new("Learn algebra", "I know arithmetic", "Solve x + 2 = 4", answer, answer is null ? null : "Mathematics"), CancellationToken.None);
        Assert.Equal("equations", Assert.Single(result.Topics).Code);
        Assert.Empty(result.Topics[0].Prerequisites);
        Assert.Equal(proposal.DiagnosticQuestion, result.DiagnosticQuestion);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("untrusted data", messages[0].GetProperty("content").GetString());
        Assert.Contains("at most one", messages[0].GetProperty("content").GetString());
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("Learn algebra", input.RootElement.GetProperty("Goal").GetString());
        Assert.Equal("I know arithmetic", input.RootElement.GetProperty("Experience").GetString());
        Assert.Equal(answer, input.RootElement.GetProperty("Answer").GetString());
        Assert.Equal(answer is null ? null : "Mathematics", input.RootElement.GetProperty("Discipline").GetString());
        Assert.Contains("preserve it exactly", messages[0].GetProperty("content").GetString());
        var format = body.RootElement.GetProperty("response_format").GetProperty("json_schema");
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.True(format.GetProperty("schema").GetProperty("properties").TryGetProperty("Topics", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateAsync_OptionalProgramContext_SendsStructuredUntrustedContext(bool withProgram)
    {
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop",
                message = new { role = "assistant", content = "{\"Theory\":\"Theory\",\"Question\":\"Question?\"}" } } }
        }));
        using var http = new HttpClient(handler);
        var context = withProgram ? new LessonProgramContext("Mathematics", "Prepare for an interview",
            "I know arithmetic", "Solve x + 2 = 4", "x = 2") : null;
        var request = new LessonRequest("Basics", context);

        var result = await Teacher(http, new RecordingLogger()).GenerateAsync(request, CancellationToken.None);

        Assert.Equal("Theory", result.Theory);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("untrusted data", messages[0].GetProperty("content").GetString());
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(request, input.RootElement.Deserialize<LessonRequest>());
    }

    [Theory]
    [InlineData(LessonInteractionKind.Clarification)]
    [InlineData(LessonInteractionKind.AlternativeExplanation)]
    public async Task ExplainAsync_DialogueRequest_SendsBoundedContextAndStructuredExplanation(LessonInteractionKind kind)
    {
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "test", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop",
                message = new { role = "assistant", content = "{\"Explanation\":\"Example\"}" } } }
        }));
        using var http = new HttpClient(handler);
        var lesson = Lesson.Start(new UserId(1), "Topic", "Theory", "Question");
        lesson.AddExplanation(LessonInteractionKind.Clarification, "Earlier request", "Earlier transcript");
        var result = await Teacher(http, new RecordingLogger()).ExplainAsync(lesson, "A detour", kind, CancellationToken.None);
        Assert.Equal("Example", result.Explanation);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("untrusted data", messages[0].GetProperty("content").GetString());
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("Topic", input.RootElement.GetProperty("Topic").GetString());
        Assert.Equal("Theory", input.RootElement.GetProperty("Theory").GetString());
        Assert.Equal("AwaitingAnswer", input.RootElement.GetProperty("Status").GetString());
        Assert.False(input.RootElement.TryGetProperty("ContextSummary", out _));
        Assert.Equal("Question", input.RootElement.GetProperty("Question").GetString());
        Assert.Equal("A detour", input.RootElement.GetProperty("Request").GetString());
        Assert.Equal(kind.ToString(), input.RootElement.GetProperty("Kind").GetString());
        Assert.DoesNotContain("Earlier transcript", handler.RequestBody);
        Assert.True(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
    }

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

        var error = await Assert.ThrowsAsync<LessonProviderException>(() => teacher.GenerateAsync(new LessonRequest("private topic"), CancellationToken.None));

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
        var error = await Assert.ThrowsAsync<LessonProviderException>(() => Teacher(http, logger).GenerateAsync(new LessonRequest("topic"), CancellationToken.None));
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => teacher.GenerateAsync(new LessonRequest("topic"), cancellation.Token));
    }

    [Fact]
    public async Task GenerateAsync_ProviderTimeout_ClassifiesAndLogsFailure()
    {
        var logger = new RecordingLogger();
        var teacher = new OpenAILessonTeacher(() => throw new OperationCanceledException(),
            Options.Create(new OpenAIInboxAnalysisOptions { Enabled = true, ApiKey = "dummy" }), logger);
        var error = await Assert.ThrowsAsync<LessonProviderException>(() => teacher.GenerateAsync(new LessonRequest("topic"), CancellationToken.None));
        Assert.Equal(LessonProviderFailure.Timeout, error.Reason);
        Assert.Contains(logger.Messages, m => m.Contains("Timeout", StringComparison.Ordinal));
    }

    private static OpenAILessonTeacher Teacher(HttpClient http, RecordingLogger logger) => new(
        () => new ChatClient("test", new ApiKeyCredential("dummy-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
        }), Options.Create(new OpenAIInboxAnalysisOptions { Enabled = true, ApiKey = "dummy-key" }), logger);

    private sealed class ResponseHandler(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        }
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
