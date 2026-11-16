using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

internal static class TelegramAdaptiveTeachingFixture
{
    internal static void Configure(IServiceCollection services, Planner planner, Teacher teacher)
    {
        services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
        services.Replace(ServiceDescriptor.Singleton<ITeachingPlanner>(planner));
        services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
    }

    internal sealed class Planner : ICurriculumPlanner, ITeachingPlanner
    {
        public CurriculumTopic[] Topics { get; init; } = [new("basics", "Basics", []), new("advanced", "Advanced", ["basics"]), new("other", "Other", [])];
        public TeachingProposal Proposal { get; set; } = new("Simplify", "basics", "Repeated negative AI assessments suggest a smaller concrete example.");
        public List<TeachingRequest> Requests { get; } = [];
        public bool Failure { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public CancellationToken Token { get; private set; }
        public Task<CurriculumProposal> PlanAsync(CurriculumRequest request, CancellationToken token) => Task.FromResult(
            new CurriculumProposal("Mathematics", Topics, null));
        public Task<TeachingProposal> ProposeAsync(TeachingRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            Token = token;
            Cancellation?.Cancel();
            if (Failure) throw new LessonProviderException();
            return Task.FromResult(Proposal);
        }
    }

    internal sealed class Teacher : ILessonTeacher
    {
        public List<LessonRequest> Requests { get; } = [];
        public CancellationTokenSource? Cancellation { get; set; }
        public CancellationToken Token { get; private set; }
        public bool Invalid { get; set; }
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            Token = token;
            Cancellation?.Cancel();
            return Task.FromResult(new LessonMaterial(Invalid ? "" : "Theory", "Question"));
        }
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) =>
            Task.FromResult(new LessonAssessment("Try a smaller example.", answer == "correct"));

        public Task<LessonAssessment> AssessAsync(LessonSnapshot lesson, string answer, CancellationToken token) =>
            Task.FromResult(new LessonAssessment("Try a smaller example.", answer == "correct"));
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) => throw new NotSupportedException();

        public Task<LessonExplanation> ExplainAsync(LessonSnapshot lesson, string request, LessonInteractionKind kind, CancellationToken token) => throw new NotSupportedException();
    }
}
