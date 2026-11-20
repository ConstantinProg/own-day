using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.OpenAIAnalysis;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed partial class OpenAILessonTeacherTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("duplicate", false)]
    [InlineData("ambiguous", false)]
    [InlineData("disputed", false)]
    [InlineData("different-key", false)]
    [InlineData("wrong-format", false)]
    public async Task GenerateAsync_ChoiceProposalAndReview_PublishesOnlyUsableProposalOrOpenFallback(string scenario, bool publishes)
    {
        var proposal = JsonSerializer.Serialize(new
        {
            Theory = "Рассмотрим сложение в другом примере.",
            EvidenceKind = "Understanding",
            SourceQuote = (string?)null,
            NeedsCurrentVerification = scenario == "disputed",
            Criteria = new[] { new { Code = "explain", Requirement = "Почему слагаемые можно переставить?" } },
            Choice = new
            {
                Format = scenario == "wrong-format" ? "TrueFalse" : "SingleChoice",
                Question = "Сколько будет два плюс два?",
                Options = new[] { new { Id = 4, Text = "четыре" }, new { Id = 2, Text = scenario == "duplicate" ? "ЧЕТЫРЕ." : "пять" } },
                CorrectOptions = new[] { 4 },
                Explanation = "Два плюс два равно четырём.",
                Review = new
                {
                    SelfContained = true, Unambiguous = scenario != "ambiguous",
                    DerivedOptions = new[] { scenario == "different-key" ? 2 : 4 }
                }
            }
        });
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "choice-response", model = "test", created = 1, @object = "chat.completion",
            usage = new { prompt_tokens = 30, completion_tokens = 10, total_tokens = 40 },
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = proposal } } }
        }));
        using var http = new HttpClient(handler);
        var usage = new LearningProviderUsage { LedgerAttemptId = Guid.NewGuid() };
        var teacher = new OpenAILessonTeacher(() => new ChatClient("test", new ApiKeyCredential("dummy"), new OpenAIClientOptions
        { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) }),
            Options.Create(new OpenAIInboxAnalysisOptions { Enabled = true, ApiKey = "dummy" }), new RecordingLogger(), usage: usage);

        var material = await teacher.GenerateAsync(new LessonRequest("Сложение") { PreferredChoice = ChoiceFormat.SingleChoice }, default);

        Assert.Equal(1, handler.Requests);
        if (publishes)
        {
            Assert.NotNull(material.Choice);
            Assert.Null(material.Content);
            Assert.Equal(new[] { 4 }, material.Choice.CorrectOptions.ToArray());
            Assert.DoesNotContain("четыр", Assert.Single(material.Choice.Hints));
        }
        else
        {
            Assert.Null(material.Choice);
            Assert.Equal("Почему слагаемые можно переставить?", material.Question);
            Assert.Equal(ExerciseEvidenceKind.Understanding, material.Content?.StructuredRubric?.Kind);
        }
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var schema = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), property => property.GetString() == "Choice");
    }
}
