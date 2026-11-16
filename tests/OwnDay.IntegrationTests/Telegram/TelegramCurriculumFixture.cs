using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

internal static class TelegramCurriculumFixture
{
    internal static async Task PostAsync(HttpClient client, int id, long user, string text)
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

    internal sealed class Planner : ICurriculumPlanner
    {
        public CancellationTokenSource? CancelAfterPlan { get; set; }
        public CancellationToken ReceivedToken { get; private set; }
        public List<CurriculumRequest> Requests { get; } = [];
        public bool Failure { get; set; }
        public bool Invalid { get; set; }
        public bool LongQuestion { get; init; }
        public bool SecondQuestion { get; set; }
        public bool DifferentDiscipline { get; set; }
        public bool NoQuestion { get; init; }
        public Task<CurriculumProposal> PlanAsync(CurriculumRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            ReceivedToken = token;
            CancelAfterPlan?.Cancel();
            if (Failure) throw new LessonProviderException();
            return Task.FromResult(new CurriculumProposal(DifferentDiscipline ? "Physics" : "Mathematics",
                Invalid ? [new("a", "A", ["a"])] : [new("advanced", "Advanced", ["basics"]), new("basics", "Basics", [])],
                NoQuestion || request.Answer is not null && !SecondQuestion ? null : LongQuestion ? new string('q', 1001) : "Solve x + 2 = 4"));
        }
    }

    internal sealed class Teacher : ILessonTeacher
    {
        public string? ExpectedAnswer { get; init; }
        public CancellationTokenSource? CancelAfterGenerate { get; set; }
        public CancellationToken ReceivedToken { get; private set; }
        public int Generated { get; private set; }
        public List<LessonRequest> Requests { get; } = [];
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            Generated++;
            ReceivedToken = token;
            CancelAfterGenerate?.Cancel();
            return Task.FromResult(new LessonMaterial("Theory", "Question") { ExpectedAnswer = ExpectedAnswer });
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) =>
            Task.FromResult(new LessonAssessment("Feedback", answer == "correct"));

        public Task<LessonAssessment> AssessAsync(LessonSnapshot lesson, string answer, CancellationToken token) =>
            Task.FromResult(new LessonAssessment("Feedback", answer == "correct"));
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<LessonExplanation> ExplainAsync(LessonSnapshot lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
