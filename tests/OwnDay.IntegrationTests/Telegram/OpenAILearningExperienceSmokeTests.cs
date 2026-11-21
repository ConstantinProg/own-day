using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Dialogue;
using OwnDay.Application.Inbox.Observability;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Learning;
using OwnDay.Infrastructure.OpenAIAnalysis;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [LearningSmokeTheory]
    [Trait("Category", "LearningLiveSmoke")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_LiveEnglishExperience_RestoresQuestionWithinExplicitCaseBudget(bool complete)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "Configure OPENAI_API_KEY for this explicit paid test.");
        var cap = SmokeAmount("OWNDAY_LEARNING_SMOKE_CASE_CAP_USD");
        Assert.InRange(cap, 0.01m, 0.50m);
        var price = new OpenAIModelPrice
        {
            InputUsdPerMillion = SmokeAmount("OWNDAY_LEARNING_SMOKE_INPUT_RATE"),
            CachedInputUsdPerMillion = SmokeAmount("OWNDAY_LEARNING_SMOKE_CACHED_RATE"),
            OutputUsdPerMillion = SmokeAmount("OWNDAY_LEARNING_SMOKE_OUTPUT_RATE")
        };
        Assert.True(price.InputUsdPerMillion > 0 && price.OutputUsdPerMillion > 0);
        var sender = new ExperienceSender();
        var probe = new Probe("");
        var id = 0;
        Guid lessonId;
        string question;
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> LiveHost() => ExperienceHost(probe, sender)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<ILessonTeacher, OpenAILessonTeacher>());
                services.Replace(ServiceDescriptor.Scoped<ILearningIntentResolver, OpenAILessonTeacher>());
                services.PostConfigure<OpenAIInboxAnalysisOptions>(options =>
                {
                    options.Enabled = true;
                    options.ApiKey = key;
                    options.TimeoutSeconds = 30;
                    options.MaxOutputTokens = 2048;
                });
                services.PostConfigure<LearningRequestProcessingOptions>(options =>
                {
                    options.ProviderTimeoutSeconds = 35;
                    options.MaxProviderAttempts = 1;
                });
                services.PostConfigure<OpenAICostOptions>(options =>
                {
                    options.MonthlyUsdLimit = cap;
                    options.MonthlyCallLimit = 4;
                    options.MonthlyUserCallLimit = 4;
                    options.MonthlyTokenLimit = 400_000;
                    options.MonthlyUserTokenLimit = 400_000;
                    options.Models["gpt-6-luna"] = price;
                });
            }));
        using (var host = LiveHost())
        using (var client = host.CreateClient())
        {
            await SendSmokeAsync(client, ++id, "Хочу читать и писать на английском");
            await SendSmokeAsync(client, ++id, "Рабочие письма");
            await SendSmokeAsync(client, ++id, "Начинаю с нуля");
            if (complete)
            {
                await SendSmokeAsync(client, ++id, "да");
                await SendSmokeAsync(client, ++id, "B");
                await SendSmokeAsync(client, ++id, "ответ: Yes, I can send the list today.");
            }
            else await SendSmokeAsync(client, ++id, "пропустим");
            await SendSmokeAsync(client, ++id, "К упражнению");
            await SendSmokeAsync(client, ++id, "Почему здесь используется could?");
            await using (var db = Open())
            {
                var lesson = await db.Set<Lesson>().Include(value => value.Interactions).SingleAsync();
                lessonId = lesson.Id;
                question = lesson.Question;
                Assert.Single(lesson.Interactions);
                Assert.Equal(AnswerAssistance.Assisted, lesson.Assistance);
            }
            await SendSmokeAsync(client, ++id, "Мне пора, продолжим позже");
            await WaitExperienceDeliveryAsync();
        }
        using (var host = LiveHost())
        using (var client = host.CreateClient())
        {
            await SendSmokeAsync(client, ++id, "Продолжим");
            await WaitExperienceDeliveryAsync();
        }
        await using var result = Open();
        var restored = await result.Set<Lesson>().SingleAsync();
        Assert.Equal(lessonId, restored.Id);
        Assert.Equal(question, restored.Question);
        Assert.Equal(LessonStatus.AwaitingAnswer, restored.Status);
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
        Assert.Empty(await result.InboxItems.ToListAsync());
        var attempts = await result.OpenAIRequestAttempts.ToListAsync();
        Assert.InRange(attempts.Count, 1, 4);
        Assert.All(attempts, attempt =>
        {
            Assert.NotNull(attempt.FinishedAt);
            Assert.NotNull(attempt.InputTokens);
            Assert.NotNull(attempt.OutputTokens);
            Assert.NotNull(attempt.ChargedUsd);
            Assert.False(string.IsNullOrWhiteSpace(attempt.PromptVersion));
        });
        Assert.True(attempts.Sum(attempt => attempt.ChargedUsd) <= cap);
        Assert.All(await result.Set<LearningProviderAttempt>().ToListAsync(), attempt => Assert.NotNull(attempt.FinishedAt));
        Assert.Equal(0, probe.Calls);
    }

    private async Task SendSmokeAsync(HttpClient client, int id, string text)
    {
        using var response = await PostPublicAsync(client, id, text);
        response.EnsureSuccessStatusCode();
        for (var attempt = 0; attempt < 900; attempt++)
        {
            await using var db = Open();
            if (await db.Set<LearningRequest>().CountAsync(request => request.Status == LearningRequestStatus.Completed) == id) return;
            await Task.Delay(50);
        }
        Assert.Fail("Live Learning request did not finish within its bounded test deadline.");
    }

    private static decimal SmokeAmount(string name)
    {
        Assert.True(decimal.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value), "Configure " + name + " explicitly with an invariant decimal.");
        Assert.InRange(value, 0m, 1000m);
        return value;
    }
}

public sealed class LearningSmokeTheoryAttribute : TheoryAttribute
{
    public LearningSmokeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("OWNDAY_RUN_LEARNING_SMOKE") != "1")
            Skip = "Paid Learning smoke requires explicit opt-in, test PostgreSQL, key, price snapshot and per-case cap.";
    }
}
