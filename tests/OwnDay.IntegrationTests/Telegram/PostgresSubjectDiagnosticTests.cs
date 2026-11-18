using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
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
