using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_SubjectDiagnosticProviderUnavailable_PersistsFailureOutcome()
    {
        var probe = new Probe("")
        {
            Plan = request => request.Question is null
                ? new("TypeScript", [new("types", "Type narrowing", [])], "Чем union отличается от intersection?")
                : throw new LessonProviderException(LessonProviderFailure.Unavailable)
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        string[] turns = ["Хочу изучить TypeScript", "Типизировать обработчики событий", "Начинаю с нуля",
            "давай", "Union допускает один из типов"];
        for (var index = 0; index < turns.Length; index++)
            await SendEnglishAsync(client, index + 1, turns[index]);

        await using var db = Open();
        var request = await db.Set<LearningRequest>().SingleAsync(row => row.SourceUpdateId == turns.Length);
        Assert.Equal(LearningRequestStatus.Completed, request.Status);
        Assert.Equal("diagnostic_provider_unavailable", request.Outcome);
    }

    [Theory]
    [InlineData("Очень смутно", false)]
    [InlineData("Что-то слышал", true)]
    [InlineData("Не уверен", false)]
    [InlineData("Кажется, понимаю", false)]
    [InlineData("Никогда не сталкивался", false)]
    [InlineData("Знаю, но объяснить не могу", false)]
    public async Task Post_SubjectDiagnosticSelfReport_KeepsLevelUnknownAndAsksFollowUp(string answer, bool proposedFollowUp)
    {
        CurriculumTopic[] topics = [new("orders", "Биржевые заявки", [])];
        var probe = new Probe("")
        {
            Plan = request =>
            {
                if (request.Question is null)
                    return new("Биржа", topics, "Чем лимитная заявка отличается от рыночной?");
                Assert.True(request.SelfReportedUncertainty);
                return new("Биржа", [new("advanced", "Арбитраж", [])],
                    proposedFollowUp ? "Кто передаёт поручение инвестора на биржу?" : null,
                    "Не знает материал.", DiagnosticStartingLevel.Foundation);
            }
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        string[] turns = ["Хочу изучить биржу", "Самостоятельно подавать заявки", "Начинаю с нуля", "давай", answer];
        for (var index = 0; index < turns.Length; index++)
            await SendEnglishAsync(client, index + 1, turns[index]);

        await using var db = Open();
        var program = await db.Set<LearningProgram>().Include(item => item.Routes).SingleAsync();
        var diagnostic = program.SubjectDiagnostic!;
        var observation = Assert.Single(diagnostic.Observations);
        Assert.Equal(answer, observation.Answer);
        Assert.True(observation.SelfReportedUncertainty);
        Assert.DoesNotContain("Не знает", observation.Interpretation);
        Assert.Equal(DiagnosticStartingLevel.Unknown, diagnostic.StartingLevel);
        Assert.True(program.DiagnosticPending);
        Assert.NotEqual(observation.Question, program.DiagnosticQuestion);
        Assert.Equal("orders", Assert.Single(program.CurrentRoute.Topics).Code);
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Empty(await db.Set<Lesson>().ToListAsync());
    }

    [Fact]
    public async Task Post_SubjectDiagnosticExhaustsFallbackQuestions_StartsLessonWithUnknownLevel()
    {
        CurriculumTopic[] topics = [new("orders", "Биржевые заявки", [])];
        var probe = new Probe("")
        {
            Plan = request => request.Question is null
                ? new("Биржа", topics, "Можете привести один простой пример по теме «Биржевые заявки»?")
                : new("Биржа", topics, request.Question, "Самооценка.", DiagnosticStartingLevel.Unknown)
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        string[] turns = ["Хочу изучить биржу", "Самостоятельно подавать заявки", "Начинаю с нуля",
            "давай", "Очень смутно", "Не уверен", "Что-то слышал", "Не знаю"];
        for (var index = 0; index < turns.Length; index++)
            await SendEnglishAsync(client, index + 1, turns[index]);

        await using var db = Open();
        var program = await db.Set<LearningProgram>().SingleAsync();
        Assert.False(program.DiagnosticPending);
        var diagnostic = program.SubjectDiagnostic!;
        Assert.Equal(DiagnosticStartingLevel.Unknown, diagnostic.StartingLevel);
        Assert.Equal(4, diagnostic.Observations.Select(item => item.Question).Distinct().Count());
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
    }

    [Fact]
    public async Task Post_SubjectDiagnosticExplicitRequestForBasics_StartsWithoutAssigningLevel()
    {
        var probe = new Probe("")
        {
            Plan = _ => new("Биржа", [new("orders", "Биржевые заявки", [])], "Как исполнится лимитная заявка?")
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        string[] turns = ["Хочу изучить биржу", "инвистиции", "Начинаю с нуля", "давай", "Давайте начнём с основ"];
        for (var index = 0; index < turns.Length; index++)
            await SendEnglishAsync(client, index + 1, turns[index]);

        await using var db = Open();
        var program = await db.Set<LearningProgram>().Include(item => item.Routes).SingleAsync();
        var diagnostic = program.SubjectDiagnostic!;
        Assert.False(program.DiagnosticPending);
        Assert.False(diagnostic.Deferred);
        Assert.Equal(DiagnosticAnswerKind.StartWithFoundations, Assert.Single(diagnostic.Observations).Kind);
        Assert.Equal(DiagnosticStartingLevel.Unknown, diagnostic.StartingLevel);
        Assert.Equal(1, program.CurrentRoute.Version);
        Assert.Equal(1, probe.KindCalls["plan"]);
        Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        Assert.Single(await db.Set<Lesson>().ToListAsync());
        Assert.Equal("инвистиции", (await db.Set<DialogueContext>().SingleAsync()).Consultation!.PracticalUse);
        Assert.Contains(await db.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
            text => text.Contains("инвестиции", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Post_SubjectDiagnosticAnswer_PublishesFollowUpAndRecoversAfterRestart()
    {
        const string first = "Чем union отличается от intersection?";
        const string second = "Как сузить union по общему полю kind?";
        const string inconclusive = "Пока не могу сформулировать разницу.";
        CurriculumTopic[] topics = [new("types", "Сужение типов", [])];
        var probe = new Probe("")
        {
            Plan = request => request.Question is null ? new("TypeScript", topics, first)
                : new("TypeScript", topics, request.Answer == inconclusive ? first : second,
                    "Предварительное наблюдение.", DiagnosticStartingLevel.Unknown)
        };
        Guid nextPrompt;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            var turns = new[] { "Хочу изучить TypeScript", "Типизировать обработчики событий", "Начинаю с нуля", "давай", inconclusive };
            for (var index = 0; index < turns.Length; index++)
                await SendEnglishAsync(client, index + 1, turns[index]);
            await using var db = Open();
            var unchanged = await db.Set<LearningProgram>().AsNoTracking().SingleAsync();
            Assert.Equal(first, unchanged.DiagnosticQuestion);
            Assert.Empty(unchanged.SubjectDiagnostic!.Observations);
            await SendEnglishAsync(client, 6, "Union допускает хотя бы один из типов, intersection требует оба.");
            Assert.Equal(second, (await db.Set<LearningProgram>().SingleAsync()).DiagnosticQuestion);
            Assert.Empty(await db.Set<Lesson>().ToListAsync());
            nextPrompt = (await db.Set<DialogueContext>().SingleAsync()).PromptId;
        }
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 7, "Продолжим");
        }
        await using var result = Open();
        Assert.Equal(nextPrompt, (await result.Set<DialogueContext>().SingleAsync()).PromptId);
        Assert.Contains(await result.TelegramOutboxMessages.Select(reply => reply.Text).ToListAsync(),
            text => text.Contains(second, StringComparison.Ordinal));
        var diagnostic = (await result.Set<LearningProgram>().SingleAsync()).SubjectDiagnostic!;
        Assert.Equal(first, Assert.Single(diagnostic.Observations).Question);
        Assert.Equal(DiagnosticStartingLevel.Unknown, diagnostic.StartingLevel);
        Assert.False(result.Database.HasPendingModelChanges());
        Assert.Empty(await result.Set<LearningEvidence>().ToListAsync());
    }

    [Fact]
    public async Task Post_SubjectDiagnosisDeferredAfterOneAnswer_CompletesLaterWithoutReplacingRoute()
    {
        const string first = "Чем лимитная заявка отличается от рыночной?";
        const string second = "Что может помешать исполнению лимитной заявки?";
        CurriculumTopic[] topics = [new("orders", "Биржевые заявки", [])];
        var probe = new Probe("")
        {
            Plan = request => request.Question switch
            {
                null => new("Биржа", topics, first),
                first => new("Биржа", topics, second, "Различает типы заявок.", DiagnosticStartingLevel.Unknown),
                _ => new("Биржа", topics, null, "Учитывает отсутствие встречной ликвидности.", DiagnosticStartingLevel.Developing)
            }
        };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        var turns = new[] { "Хочу понять, как работают биржевые заявки", "Понимать механику исполнения заявок",
            "Начинаю с нуля", "да", "Лимитная ограничивает цену исполнения.", "позже" };
        for (var index = 0; index < turns.Length; index++)
            await SendEnglishAsync(client, index + 1, turns[index]);

        await using var before = Open();
        var deferred = await before.Set<LearningProgram>().AsNoTracking().SingleAsync();
        Assert.False(deferred.DiagnosticPending);
        Assert.True(deferred.SubjectDiagnostic!.Deferred);
        Assert.Equal(first, Assert.Single(deferred.SubjectDiagnostic.Observations).Question);

        await SendEnglishAsync(client, 7, "/learn_diagnose start");
        Assert.False((await before.Set<LearningProgram>().AsNoTracking().SingleAsync()).DiagnosticPending);
        await SendEnglishAsync(client, 8, "/learn_finish");
        await SendEnglishAsync(client, 9, "подтверждаю " + await SwitchCodeAsync());
        await SendEnglishAsync(client, 10, "вернуться к диагностике");
        await SendEnglishAsync(client, 11, "Может не быть встречной заявки по подходящей цене.");

        var completed = await before.Set<LearningProgram>().Include(program => program.Routes).AsNoTracking().SingleAsync();
        Assert.False(completed.DiagnosticPending);
        Assert.False(completed.SubjectDiagnostic!.Deferred);
        Assert.Equal(DiagnosticStartingLevel.Developing, completed.SubjectDiagnostic.StartingLevel);
        Assert.Equal(new[] { first, second }, completed.SubjectDiagnostic.Observations.Select(item => item.Question));
        Assert.Equal(1, completed.CurrentRoute.Version);
        Assert.Empty(await before.Set<LearningEvidence>().ToListAsync());
    }
}
