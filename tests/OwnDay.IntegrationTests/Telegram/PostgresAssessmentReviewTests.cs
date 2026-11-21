using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_AppealVerifiesAgainstOriginalRubric_PreservesInitialGradeAndRecalculatesProgress()
    {
        var probe = new Probe("")
        {
            Material = new("Stable keys identify corresponding items.", "Why use stable keys?")
            {
                Content = new("keys", "rubric-v1", LearningContentKind.StructuredExercise,
                    "Understand stable keys", "Explain identity", "Explain how a key preserves identity across reordering.",
                    new(1, ExerciseEvidenceKind.Understanding, [new("identity", "Explain identity across reordering.")]))
            },
            Assessment = (snapshot, _) => new LessonAssessment("Assessment basis", snapshot.Review is not null)
            {
                Rubric = new([new("identity", snapshot.Review is null ? RubricCriterionResult.Unclear : RubricCriterionResult.Met,
                    "The answer connects stable identity to reordering.")])
            }
        };
        Guid id;
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            string[] turns = ["Хочу изучить TypeScript", "Разбирать списки React", "Начинаю с нуля", "Продолжим", "К упражнению",
                "/answer React keeps each item's identity.", "/answer The key persists when the list is reordered."];
            for (var index = 0; index < turns.Length; index++) await SendEnglishAsync(client, index + 1, turns[index]);
            await using var db = Open();
            var original = await db.Set<LearningEvidence>().SingleAsync();
            id = original.LessonId;
            Assert.Equal(AnswerAssessmentStatus.Inconclusive, original.AssessmentStatus);
            Assert.Empty(await db.Set<ReviewSchedule>().ToListAsync());
        }

        var appeal = $"/learn_appeal {id} | 0 | Связь с перестановкой указана в уточнении";
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 8, "оспорить оценку");
            Assert.Equal(2, probe.AssessedAnswers.Count);
        }
        using (var host = Host(probe, worker: false, publicIngress: true, interceptor: new RejectBoundary("apply"),
            configure: options => { options.Enabled = true; options.DialogueEnabled = true; }))
        using (var client = host.CreateClient())
        {
            using var accepted = await PostPublicAsync(client, 9, appeal);
            accepted.EnsureSuccessStatusCode();
            using var scope = host.Services.CreateScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        }
        await using (var db = Open())
        {
            Assert.Null((await db.Set<LearningEvidence>().SingleAsync()).ReviewHistory);
            Assert.Empty(await db.Set<ReviewSchedule>().ToListAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE learning_requests SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 2");
        }
        using (var host = ConsultationHost(probe))
        using (var client = host.CreateClient())
        {
            await WaitAllPublicAsync(9);
            await SendEnglishAsync(client, 10, $"/learn_appeal {id} | 0 | Другая формулировка");
            using var duplicate = await PostPublicAsync(client, 9, appeal);
            duplicate.EnsureSuccessStatusCode();
            await SendEnglishAsync(client, 11, "/learn_progress");
            await TelegramCurriculumFixture.PostAsync(client, 12, 202, $"/learn_appeal {id} | 1 | Попытка другого владельца");
            await WaitAllPublicAsync(12);
        }

        await using var result = Open();
        var evidence = await result.Set<LearningEvidence>().SingleAsync();
        Assert.Equal(AnswerAssessmentStatus.Inconclusive, evidence.AssessmentStatus);
        Assert.False(evidence.AssessedCorrect);
        Assert.True(evidence.EffectiveCorrect);
        Assert.Equal(AssessmentReviewOutcome.Changed, Assert.Single(evidence.ReviewHistory!.Revisions).Outcome);
        Assert.Equal(evidence.ObservedAt.AddDays(1), (await result.Set<ReviewSchedule>().SingleAsync()).DueAt);
        Assert.Equal(SkillMastery.Developing, LearnerSkillState.Evaluate([evidence]).Mastery);
        Assert.Equal(4, probe.AssessedAnswers.Count);
        var reviews = probe.AssessedLessons.Where(snapshot => snapshot.Review is not null).ToArray();
        Assert.Equal(new[] { "Reassessment", "Independent verification" }, reviews.Select(snapshot => snapshot.Review!.Stage));
        Assert.All(reviews, snapshot => Assert.Null(snapshot.RubricEvaluation));
        Assert.Contains(await result.TelegramOutboxMessages.Select(message => message.Text).ToListAsync(),
            text => text.Contains("Route questions credited: 1/1", StringComparison.Ordinal));
        Assert.False(result.Database.HasPendingModelChanges());
    }
}
