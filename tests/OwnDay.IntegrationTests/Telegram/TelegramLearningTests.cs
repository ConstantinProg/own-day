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
        Assert.Equal(3, await db.TelegramOutboxMessages.CountAsync());
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
