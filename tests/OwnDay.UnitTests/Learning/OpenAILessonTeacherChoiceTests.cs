using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.OpenAIAnalysis;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed partial class OpenAILessonTeacherTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterpretChoiceAsync_ProviderSelectionOrAmbiguity_ReturnsOnlySelectedIds(bool matched)
    {
        var response = JsonSerializer.Serialize(new { SelectedOptions = matched ? new[] { 4 } : null });
        using var handler = new ResponseHandler(JsonSerializer.Serialize(new
        {
            id = "interpretation", model = "test", created = 1, @object = "chat.completion",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = response } } }
        }));
        using var http = new HttpClient(handler);
        var input = new ChoiceInterpretationInput("Choose a sum.", ChoiceFormat.SingleChoice,
            [new(4, "Two plus two"), new(2, "Two plus three")], "The sum of two twos.");

        var result = await Teacher(http, new RecordingLogger()).InterpretChoiceAsync(input, default);

        Assert.Equal(matched ? new[] { 4 } : null, result.SelectedOptions?.ToArray());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        using var sent = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(input.Answer, sent.RootElement.GetProperty("Answer").GetString());
        Assert.Equal(2, sent.RootElement.GetProperty("Options").GetArrayLength());
        Assert.False(sent.RootElement.TryGetProperty("CorrectOptions", out _));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("duplicate", false)]
    [InlineData("ambiguous", false)]
    [InlineData("disputed", false)]
    [InlineData("different-key", false)]
    [InlineData("wrong-format", false)]
    [InlineData("missing-guidance", false)]
    [InlineData("spoiler", false)]
    public async Task GenerateAsync_ChoiceProposalAndReview_PublishesOnlyUsableProposalOrOpenFallback(string scenario, bool publishes)
    {
        const string theory = "Сложение объединяет количества. Если в коробках два и три карандаша, вместе их пять.";
        var proposal = JsonSerializer.Serialize(new
        {
            Theory = theory,
            EvidenceKind = "Understanding",
            SourceQuote = (string?)null,
            NeedsCurrentVerification = scenario == "disputed",
            Criteria = new[] { new { Code = "explain", Requirement = "Объясните, как найти общее число карандашей в двух коробках." } },
            Choice = new
            {
                Format = scenario == "wrong-format" ? "TrueFalse" : "SingleChoice",
                Question = "Сколько будет два плюс два?",
                Options = new[] { new { Id = 4, Text = scenario == "spoiler" ? "ровно четыре карандаша" : "четыре" },
                    new { Id = 2, Text = scenario == "duplicate" ? "ЧЕТЫРЕ." : "пять" } },
                CorrectOptions = new[] { 4 },
                Explanation = "Два плюс два равно четырём.",
                Guidance = scenario == "missing-guidance" ? Array.Empty<ChoiceGuidance>() :
                    new[] { new ChoiceGuidance(2, "Проверьте, добавляли ли ещё один предмет.", scenario == "spoiler"
                        ? "Получится ровно четыре карандаша." : "Представьте две коробки с двумя предметами в каждой.") },
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

        var program = new LessonProgramContext("Математика", "Научиться считать покупки",
            "Применение: подсчёт товаров в корзине\nОпыт: С нуля", null, null)
        { StartingLevel = DiagnosticStartingLevel.Foundation, PracticalUse = "подсчёт товаров в корзине" };
        var material = await teacher.GenerateAsync(new LessonRequest("Сложение", program) { PreferredChoice = ChoiceFormat.SingleChoice }, default);

        Assert.Equal(1, handler.Requests);
        Assert.Equal(theory, material.Theory);
        if (publishes)
        {
            Assert.NotNull(material.Choice);
            Assert.Null(material.Content);
            Assert.Equal(new[] { 4 }, material.Choice.CorrectOptions.ToArray());
            var guidance = Assert.Single(material.Choice.Guidance);
            Assert.Equal(2, guidance.OptionId);
            Assert.NotEqual(guidance.Hint, guidance.Example);
            Assert.DoesNotContain("четыр", guidance.Hint);
        }
        else
        {
            Assert.Null(material.Choice);
            Assert.Equal("Объясните, как найти общее число карандашей в двух коробках.", material.Question);
            Assert.Equal(ExerciseEvidenceKind.Understanding, material.Content?.StructuredRubric?.Kind);
        }
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        var instructions = messages[0].GetProperty("content").GetString();
        Assert.Contains("same idea taught in Theory", instructions);
        Assert.Contains("Adjust difficulty based on the supplied evidence for the current topic", instructions);
        Assert.Contains("For Foundation, assume no prior subject vocabulary", instructions);
        Assert.Contains("Use only terms introduced in Theory or everyday words in the exercise question and all answer options", instructions);
        using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        var context = input.RootElement.GetProperty("Program");
        Assert.Equal("Foundation", context.GetProperty("StartingLevel").GetString());
        Assert.Equal(program.Goal, context.GetProperty("Goal").GetString());
        Assert.Equal(program.Experience, context.GetProperty("Experience").GetString());
        Assert.Equal(program.PracticalUse, context.GetProperty("PracticalUse").GetString());
        var schema = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), property => property.GetString() == "Choice");
    }
}
