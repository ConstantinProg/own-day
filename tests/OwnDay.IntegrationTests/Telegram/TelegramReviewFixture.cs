using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

internal static class TelegramReviewFixture
{
    internal static void Configure(IServiceCollection services, Clock clock, Teacher teacher)
    {
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed class Teacher : ILessonTeacher
    {
        public List<LessonRequest> Requests { get; } = [];
        public int Assessed { get; private set; }
        public string Failure { get; set; } = "";
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Failure == "failure") throw new LessonProviderException();
            return Task.FromResult(new LessonMaterial(Failure == "invalid" ? "" : "Context",
                request.IsReview ? "A fresh application question" : "Original question"));
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assessed++;
            return Task.FromResult(new LessonAssessment("AI basis", answer == "correct") { Source = "test-model" });
        }

        public Task<LessonAssessment> AssessAsync(LessonSnapshot lesson, string answer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assessed++;
            return Task.FromResult(new LessonAssessment("AI basis", answer == "correct") { Source = "test-model" });
        }
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<LessonExplanation> ExplainAsync(LessonSnapshot lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
