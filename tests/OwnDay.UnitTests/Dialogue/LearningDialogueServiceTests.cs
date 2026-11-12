using System.Text.Json;
using OwnDay.Application.Dialogue;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Dialogue;

public sealed class LearningDialogueServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Stamp = new('A', 64);
    private static readonly UserId Owner = new(101);

    [Fact]
    public async Task Decide_GoldenDialogueCases_EnforcesExpectedActionsWithoutLearningMutation()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "LearningGoldenDataset", "golden-v1.json"));
        var dataset = JsonSerializer.Deserialize<Dataset>(json, JsonSerializerOptions.Web)!;
        Assert.Equal("learning-dialogue-v1", dataset.Version);
        Assert.Equal(dataset.Scenarios.Length, dataset.Scenarios.Select(value => value.Id).Distinct().Count());
        foreach (var scenario in dataset.Scenarios)
        {
            var lesson = Lesson.Start(Owner, "English", "Read this", scenario.Question ?? "Write one sentence about a work email.");
            if (scenario.Status == "Paused") lesson.Pause();
            var store = new Store { Context = DialogueContext.Create(Owner, lesson, Now) };
            var resolver = new Resolver(scenario.Proposal is null ? LearningIntent.Ambiguous : Enum.Parse<LearningIntent>(scenario.Proposal));
            var turn = new DialogueTurn(Guid.NewGuid(), Owner, null, scenario.Input, lesson.Id, store.Context.PromptId, Now.AddSeconds(1), Now.AddSeconds(1));
            var result = await new LearningDialogueService(store, resolver).DecideAsync(turn, lesson, null, Stamp, default);
            Assert.True(result.Execute?.ToString() == scenario.Action, scenario.Id + ": unexpected action " + result.Execute);
            Assert.True(resolver.Calls == scenario.Calls, scenario.Id + ": unexpected classifier calls " + resolver.Calls);
            if (scenario.Pending is not null) Assert.Equal(scenario.Pending, result.Context.ExpectedResponse.ToString());
            else
            {
                Assert.Null(result.Context.PendingClarification);
                Assert.Null(result.Context.PendingConfirmation);
            }
            if (scenario.Allowed is not null) Assert.Equal(scenario.Allowed, result.Context.PendingClarification!.Allowed.Select(value => value.ToString()));
            Assert.Equal(scenario.Status == "Paused" ? LessonStatus.Paused : LessonStatus.AwaitingAnswer, lesson.Status);
            Assert.Null(lesson.Answer);
        }
    }

    [Fact]
    public async Task Decide_ResolvedClarification_UsesOriginalInputWithoutCallingClassifierAgain()
    {
        var lesson = Lesson.Start(Owner, "English", "Theory", "Write a polite request.");
        var originalId = Guid.NewGuid();
        var context = DialogueContext.Create(Owner, lesson, Now);
        var originalPrompt = context.PromptId;
        context.OpenClarification(originalId, [DialogueOperationKind.Answer, DialogueOperationKind.Clarify], Stamp, Now);
        var store = new Store { Context = context, Original = new("Could you help me?", lesson.Id, originalPrompt) };
        var resolver = new Resolver(LearningIntent.Answer);
        var result = await new LearningDialogueService(store, resolver).DecideAsync(new(Guid.NewGuid(), Owner, null, "ответ",
            lesson.Id, context.PromptId, Now.AddSeconds(1), Now.AddSeconds(1)), lesson, null, Stamp, default);
        Assert.Equal(DialogueOperationKind.Answer, result.Execute);
        Assert.Equal("Could you help me?", result.Arguments);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Decide_StalePromptOrInvalidProposal_DoesNotAuthorizeAnswer()
    {
        var lesson = Lesson.Start(Owner, "English", "Theory", "Question");
        var store = new Store { Context = DialogueContext.Create(Owner, lesson, Now) };
        var resolver = new Resolver((LearningIntent)999);
        var service = new LearningDialogueService(store, resolver);
        var turn = new DialogueTurn(Guid.NewGuid(), Owner, null, "Some response", lesson.Id, Guid.NewGuid(), Now, Now);
        Assert.Null((await service.DecideAsync(turn, lesson, null, Stamp, default)).Execute);
        Assert.Equal(0, resolver.Calls);
        var result = await service.DecideAsync(turn with { ReceiptPromptId = store.Context.PromptId }, lesson, null, Stamp, default);
        Assert.Null(result.Execute);
        Assert.NotNull(result.Context.PendingClarification);
        Assert.Equal(1, resolver.Calls);
    }

    private sealed record Dataset(string Version, Scenario[] Scenarios);
    private sealed record Scenario(string Id, string Input, string? Question, string? Status, string? Proposal,
        string? Action, string? Pending, int Calls, string[]? Allowed);
    private sealed class Resolver(LearningIntent intent) : ILearningIntentResolver
    {
        public int Calls { get; private set; }
        public Task<LearningIntentProposal> ResolveAsync(LearningIntentInput input, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new LearningIntentProposal(intent)); }
    }
    private sealed class Store : IDialogueStore
    {
        public DialogueContext? Context { get; set; }
        public DialogueOriginalInput? Original { get; init; }
        public Task<DialogueContext?> FindAsync(UserId user, CancellationToken token) => Task.FromResult(Context);
        public void Add(DialogueContext context) => Context = context;
        public Task<DialogueProgramSnapshot?> ReadProgramAsync(UserId user, CancellationToken token) => Task.FromResult<DialogueProgramSnapshot?>(null);
        public Task<DialogueProgramSnapshot?> ReadProgramByIdAsync(UserId user, Guid programId, CancellationToken token) => Task.FromResult<DialogueProgramSnapshot?>(null);
        public Task<DialogueOriginalInput?> ReadOriginalAsync(UserId user, Guid requestId, CancellationToken token) => Task.FromResult(Original);
    }
}
