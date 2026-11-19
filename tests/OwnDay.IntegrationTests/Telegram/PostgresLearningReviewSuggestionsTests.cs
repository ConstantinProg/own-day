using Microsoft.EntityFrameworkCore;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_ResumeWithDueReviewsAndPrerequisite_OffersOrderedOptionsWithoutNotificationConsent()
    {
        var program = LearningProgram.Create(_user, "Work with union types", "Beginner", null, "TypeScript",
            [new("a", "Introduction", []), new("base", "Foundations", []),
                new("next", "Handlers", ["a", "base"]), new("practice", "Practice", ["a", "base"])], null);
        var foundation = Skill.Create(_user, "Foundations");
        var earliest = Skill.Create(_user, "Earliest review");
        var later = Skill.Create(_user, "Second review");
        await using (var db = Open())
        {
            db.AddRange(program, LearningContext.Create(program), foundation, earliest, later);
            var previous = LearningSession.Start(_user, program.Id, _now.AddDays(-1), 1, null);
            previous.End(_now.AddDays(-1), LearningSessionEnd.Explicit);
            db.Add(previous);

            foreach (var (skill, at) in new[] { (earliest, _now.AddDays(-4)), (later, _now.AddDays(-3)), (foundation, _now.AddHours(-1)) })
            {
                var lesson = Lesson.Start(_user, skill.Title, "Theory", "Question");
                lesson.LinkToSkill(skill);
                if (skill == foundation) lesson.LinkToProgram(program, "base");
                if (skill == earliest) lesson.LinkToProgram(program, "a");
                lesson.Complete("Answer", "Feedback", true);
                var evidence = LearningEvidence.FromAnswer(lesson, at, "test");
                db.AddRange(lesson, evidence, ReviewSchedule.FromAnswer(evidence));
            }
            await db.SaveChangesAsync();
        }

        var probe = new Probe("") { Assessment = (_, _) => new("Check the definition", false) };
        using var host = ConsultationHost(probe);
        using var client = host.CreateClient();
        await SendEnglishAsync(client, 1, "Продолжим");
        await using (var db = Open())
        {
            var replies = await db.TelegramOutboxMessages.OrderBy(reply => reply.ReplyOrdinal).Select(reply => reply.Text).ToListAsync();
            var suggestions = replies.Where(reply => reply.Contains("/learn_review ", StringComparison.Ordinal) ||
                reply.Contains("/learn_practice ", StringComparison.Ordinal)).ToArray();
            Assert.Collection(suggestions,
                reply => Assert.Contains($"/learn_review {earliest.Id}", reply, StringComparison.Ordinal),
                reply => Assert.Contains($"/learn_review {later.Id}", reply, StringComparison.Ordinal),
                reply =>
                {
                    Assert.Contains($"/learn_practice {foundation.Id}", reply, StringComparison.Ordinal);
                    Assert.Contains("Handlers", reply, StringComparison.Ordinal);
                });
            Assert.Empty(await db.Set<StudySchedule>().ToListAsync());
            Assert.Equal(3, await db.Set<Lesson>().CountAsync());
            Assert.Equal(0, probe.Calls);
        }

        await SendEnglishAsync(client, 2, $"/learn_review_later {earliest.Id}");
        await SendEnglishAsync(client, 3, "what should I review?");
        await SendEnglishAsync(client, 4, $"/learn_review {later.Id}");
        await SendEnglishAsync(client, 5, $"/learn_review {later.Id}");
        await SendEnglishAsync(client, 6, "/answer Incorrect answer");

        await using var result = Open();
        var followUp = (await result.Set<LearningRequest>().SingleAsync(request => request.SourceUpdateId == 3)).Id;
        var followUpReplies = await result.TelegramOutboxMessages.Where(reply => reply.LearningRequestId == followUp).Select(reply => reply.Text).ToArrayAsync();
        Assert.Contains(followUpReplies, reply => reply.Contains(later.Id.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(followUpReplies, reply => reply.Contains(earliest.Id.ToString(), StringComparison.Ordinal));
        var observations = await result.Set<LearningEvidence>().Where(evidence => evidence.SkillId == later.Id).ToArrayAsync();
        Assert.Equal(2, observations.Length);
        Assert.Equal(SkillMastery.Conflicting, LearnerSkillState.Evaluate(observations).Mastery);
        Assert.Equal(_now.AddHours(1), (await result.Set<ReviewSchedule>().SingleAsync(schedule => schedule.SkillId == later.Id)).DueAt);
        Assert.Equal(_now.AddDays(1), (await result.Set<ReviewSchedule>().SingleAsync(schedule => schedule.SkillId == earliest.Id)).DueAt);
        Assert.Equal(1, await result.Set<LearningEvidence>().CountAsync(evidence => evidence.SkillId == earliest.Id));
        Assert.Equal(1, probe.KindCalls["generate"]);
    }
}
