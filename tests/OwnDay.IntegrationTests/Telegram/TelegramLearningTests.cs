using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed class TelegramLearningTests
{
    [Fact]
    public async Task Post_DetourPauseResumeAndFinish_PreservesStepAndRejectsInvalidTransitions()
    {
        using var factory = new OwnDayHostFactory();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher))));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/learn algebra");
        await PostAsync(client, 2, 101, "/learn_ask How is this used in physics?");
        await PostAsync(client, 2, 101, "/learn_ask How is this used in physics?");
        await PostAsync(client, 3, 101, "/learn_explain");
        await PostAsync(client, 4, 101, "/learn_pause");
        await PostAsync(client, 4, 101, "/learn_pause");
        await PostAsync(client, 5, 101, "/answer four");
        await PostAsync(client, 6, 101, "/learn_ask Explain while paused");
        await PostAsync(client, 7, 202, "/learn_explain");
        await PostAsync(client, 8, 101, "/learn other topic");
        await PostAsync(client, 9, 101, "/learn_resume");
        await PostAsync(client, 10, 101, "/learn_finish");
        await PostAsync(client, 10, 101, "/learn_finish");
        await PostAsync(client, 11, 101, "/answer four");
        await PostAsync(client, 12, 101, "/learn_resume");
        await PostAsync(client, 13, 101, "/learn geometry");
        await PostAsync(client, 14, 101, "/answer four");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var lessons = await db.Set<Lesson>().Include(l => l.Interactions).ToListAsync();
        var finished = Assert.Single(lessons, l => l.Status == LessonStatus.Finished);
        Assert.Equal("algebra", finished.Topic);
        Assert.Equal("Two plus two?", finished.Question);
        Assert.Null(finished.Answer);
        Assert.Null(finished.IsCorrect);
        Assert.Equal(2, finished.Interactions.Count);
        Assert.Contains(finished.Interactions, i => i.Request == "How is this used in physics?");
        Assert.Contains(finished.Interactions, i => i.Kind == LessonInteractionKind.AlternativeExplanation);
        Assert.Equal("geometry", Assert.Single(lessons, l => l.Status == LessonStatus.Completed).Topic);
        Assert.Equal(2, teacher.Generated);
        Assert.Equal(2, teacher.Explained);
        Assert.Equal(1, teacher.Assessed);
        Assert.Equal(14, await db.ProcessedTelegramUpdates.CountAsync());
        var replies = await db.TelegramOutboxMessages.ToListAsync();
        Assert.Equal(18, replies.Count);
        Assert.Contains(replies, r => r.Text.Contains("Return to: algebra\nTwo plus two?", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Text.Contains("Topic: algebra\nExpected action: Answer the current question", StringComparison.Ordinal));
        Assert.All(replies.Where(r => r.ChatId == 202), r => Assert.DoesNotContain("algebra", r.Text));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Post_ExplanationFailureOrInvalidOutput_PreservesLessonAndHistory(bool failure)
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(new Teacher
            {
                ExplanationFailure = failure, InvalidExplanation = !failure
            }))));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/learn algebra");
        await PostAsync(client, 2, 101, "/learn_ask Why?");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var lesson = Assert.Single(await db.Set<Lesson>().Include(l => l.Interactions).ToListAsync());
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        Assert.Empty(lesson.Interactions);
        Assert.Equal("Two plus two?", lesson.Question);
        Assert.Contains(await db.TelegramOutboxMessages.ToListAsync(), r => r.Text.Contains("Retry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_LessonAndAnswer_PersistsIsolatesAndDeduplicates()
    {
        using var factory = new OwnDayHostFactory();
        var teacher = new Teacher();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher))));
        using var client = host.CreateClient();
        await PostAsync(client, 1, 101, "/learn algebra");
        await PostAsync(client, 1, 101, "/learn algebra");
        await PostAsync(client, 2, 202, "/answer other user's answer");
        await PostAsync(client, 3, 101, "/answer four");
        await PostAsync(client, 3, 101, "/answer four");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var lesson = Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Equal("four", lesson.Answer);
        Assert.True(lesson.IsCorrect);
        Assert.Equal("Good", lesson.Feedback);
        Assert.Equal(4, await db.TelegramOutboxMessages.CountAsync());
        Assert.Equal(1, teacher.Generated);
        Assert.Equal(1, teacher.Assessed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Post_ProviderFailureOrInvalidMaterial_DoesNotCreateLesson(bool failure)
    {
        using var factory = new OwnDayHostFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(new Teacher { Failure = failure, Invalid = !failure }))));
        using var client = host.CreateClient();
        await PostAsync(client, 11, 101, "/learn algebra");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
        Assert.Contains("Retry", Assert.Single(await db.TelegramOutboxMessages.ToListAsync()).Text);
    }

    private static async Task PostAsync(HttpClient client, int id, long user, string text)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook");
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "integration-test-webhook-secret");
        request.Content = JsonContent.Create(new
        {
            update_id = id,
            message = new { message_id = id, date = 1700000000, chat = new { id = user, type = "private" },
                from = new { id = user, is_bot = false, first_name = "Learner" }, text }
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class Teacher : ILessonTeacher
    {
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Explained++;
            if (ExplanationFailure) throw new LessonProviderException();
            return Task.FromResult(new LessonExplanation(InvalidExplanation ? "" : "Another explanation"));
        }
        public int Explained { get; private set; }
        public bool ExplanationFailure { get; init; }
        public bool InvalidExplanation { get; init; }
        public int Generated { get; private set; }
        public int Assessed { get; private set; }
        public bool Failure { get; init; }
        public bool Invalid { get; init; }
        public Task<LessonMaterial> GenerateAsync(string topic, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Generated++;
            if (Failure) throw new LessonProviderException();
            return Task.FromResult(new LessonMaterial(Invalid ? "" : "Theory", "Two plus two?"));
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assessed++;
            return Task.FromResult(new LessonAssessment("Good", true));
        }
    }
}
