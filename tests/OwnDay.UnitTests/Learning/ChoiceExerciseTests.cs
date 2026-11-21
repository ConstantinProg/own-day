using System.Collections.Immutable;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class ChoiceExerciseTests
{
    [Theory]
    [InlineData(ChoiceFormat.SingleChoice, "А", ChoiceOutcome.Correct)]
    [InlineData(ChoiceFormat.SingleChoice, "B", ChoiceOutcome.Incorrect)]
    [InlineData(ChoiceFormat.MultipleChoice, "Б, А", ChoiceOutcome.Correct)]
    [InlineData(ChoiceFormat.MultipleChoice, "A, C", ChoiceOutcome.Partial)]
    [InlineData(ChoiceFormat.MultipleChoice, "C", ChoiceOutcome.Incorrect)]
    [InlineData(ChoiceFormat.MultipleChoice, "A, B, C", ChoiceOutcome.Incorrect)]
    [InlineData(ChoiceFormat.TrueFalse, "Верно", ChoiceOutcome.Correct)]
    [InlineData(ChoiceFormat.TrueFalse, "false", ChoiceOutcome.Incorrect)]
    public async Task AnswerAsync_TextSelection_GradesAgainstPublishedKey(ChoiceFormat format, string answer, ChoiceOutcome expected)
    {
        var (lesson, learning, knowledge) = Create(format);

        await learning.AnswerAsync(lesson.UserId, answer, default);

        var attempt = Assert.Single(lesson.Choice!.Attempts);
        Assert.Equal(expected, attempt.Outcome);
        Assert.Equal(AnswerAssistance.Independent, attempt.Assistance);
        if (expected == ChoiceOutcome.Correct)
        {
            var evidence = Assert.Single(knowledge.Evidence);
            Assert.Equal(lesson.Id, evidence.LessonId);
            Assert.Equal(ExerciseEvidenceKind.Recognition, evidence.ChoiceEvidence!.Kind);
            Assert.True(evidence.DeterministicIsCorrect);
            Assert.Null(evidence.AiAssessedCorrect);
            Assert.Equal(SkillMastery.Unknown, LearnerSkillState.Evaluate([evidence]).Mastery);
        }
        else if (format != ChoiceFormat.TrueFalse)
        {
            Assert.Empty(knowledge.Evidence);
            Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        }
        else
        {
            Assert.Equal(LessonStatus.Completed, lesson.Status);
            Assert.Single(knowledge.Evidence);
        }
    }

    [Fact]
    public async Task AnswerAsync_PartialThenCorrectAfterHint_PreservesBothAttemptsAndInitialDifficulty()
    {
        var (lesson, learning, knowledge) = Create(ChoiceFormat.MultipleChoice);
        var first = Assert.IsType<LearningResult.ChoiceReply>(await learning.AnswerAsync(lesson.UserId, "А", default));
        Assert.Contains("частично", first.Message);
        Assert.Contains("Сопоставьте", first.Message);
        Assert.DoesNotContain("Правильный выбор", first.Message);
        Assert.NotNull(first.Prompt);

        var second = Assert.IsType<LearningResult.ChoiceReply>(await learning.AnswerAsync(lesson.UserId, "А, Б", default));
        Assert.Contains("с помощью", second.Message);
        Assert.Null(second.Prompt);
        var attempts = lesson.Choice!.Attempts;
        Assert.Equal([ChoiceOutcome.Partial, ChoiceOutcome.Correct], attempts.Select(item => item.Outcome));
        Assert.Equal([0, 1], attempts.Select(item => item.HintsUsed));
        Assert.Equal(new[] { 2, 5 }, attempts[1].Selected.ToArray());
        var evidence = Assert.Single(knowledge.Evidence);
        Assert.Equal(2, evidence.ChoiceEvidence!.Attempts);
        Assert.Equal(AnswerAssistance.Assisted, evidence.Assistance);
        Assert.Equal(SkillMastery.NeedsPractice, LearnerSkillState.Evaluate([evidence]).Mastery);
    }

    [Fact]
    public async Task AnswerAsync_ReusedQuestion_RecordsAssistanceWithoutIndependentCredit()
    {
        var (lesson, learning, knowledge) = Create(ChoiceFormat.SingleChoice, reused: true);
        await learning.AnswerAsync(lesson.UserId, "A", default);

        var attempt = Assert.Single(lesson.Choice!.Attempts);
        Assert.False(attempt.SolutionSeen);
        Assert.True(attempt.Reused);
        Assert.Equal(AnswerAssistance.Assisted, attempt.Assistance);
        Assert.Equal(0, LearnerSkillState.Evaluate(knowledge.Evidence).PositiveAssessments);
    }

    [Fact]
    public async Task RevealAsync_BeforeAnAttempt_OffersFreshCheckAndDoesNotCreditReproduction()
    {
        var (lesson, learning, knowledge) = Create(ChoiceFormat.SingleChoice);
        var reveal = Assert.IsType<LearningResult.ChoiceReply>(await learning.RevealAsync(lesson.UserId, default));
        Assert.Equal(LearningContinuation.Practice, reveal.Prompt?.AfterSolution);
        Assert.Contains(lesson.Choice!.Exercise.Explanation, reveal.Message);

        await learning.AnswerAsync(lesson.UserId, "A", default);

        Assert.Empty(lesson.Choice.Attempts);
        Assert.Empty(knowledge.Evidence);
    }

    [Fact]
    public async Task AnswerAsync_ChoiceInReview_DoesNotExtendIntervalsOrOfferInstructionalRetry()
    {
        var (lesson, learning, knowledge) = Create(ChoiceFormat.SingleChoice, review: true);
        var skill = Assert.Single(knowledge.Skills);
        var prior = Lesson.Start(lesson.UserId, "Тема", "Теория", "Объясните принцип.");
        prior.LinkToSkill(skill);
        prior.Complete("Ответ", "Верно", true);
        var priorEvidence = LearningEvidence.FromAnswer(prior, DateTimeOffset.UnixEpoch, "Test");
        knowledge.Add(priorEvidence);
        knowledge.Add(ReviewSchedule.FromAnswer(priorEvidence));
        var due = Assert.Single(knowledge.Reviews).DueAt;

        var reply = Assert.IsType<LearningResult.ChoiceReply>(await learning.AnswerAsync(lesson.UserId, "A", default));
        Assert.Null(reply.Prompt);
        Assert.Equal(due, Assert.Single(knowledge.Reviews).DueAt);
        Assert.Equal(0, Assert.Single(knowledge.Reviews).SuccessfulReviews);
        Assert.False(LearnerSkillState.HasRecentRetention(knowledge.Evidence, DateTimeOffset.UnixEpoch.AddDays(10), TimeSpan.FromDays(30)));

        var (wrong, otherLearning, otherKnowledge) = Create(ChoiceFormat.SingleChoice, review: true);
        var wrongReply = Assert.IsType<LearningResult.ChoiceReply>(await otherLearning.AnswerAsync(wrong.UserId, "B", default));
        Assert.Null(wrongReply.Prompt);
        Assert.Equal(LessonInteractionKind.FullAnswer, Assert.Single(wrong.Interactions).Kind);
        Assert.Single(otherKnowledge.Evidence);
    }

    [Fact]
    public void Validate_InvalidFormatKeyOptionsOrLimits_RejectsPublication()
    {
        var valid = Exercise(ChoiceFormat.MultipleChoice);
        ChoiceExercise[] invalid =
        [
            valid with { Version = 0 }, valid with { Format = (ChoiceFormat)999 },
            valid with { CorrectOptions = [] }, valid with { CorrectOptions = [2, 2] },
            valid with { CorrectOptions = [2, 6] }, valid with { CorrectOptions = [2] },
            valid with { CorrectOptions = [2, 5, 4] }, valid with { Format = ChoiceFormat.SingleChoice },
            valid with { Options = [new(2, "один"), new(2, "два"), new(4, "три")] },
            valid with { Options = [new(2, "один."), new(5, "  ОДИН "), new(4, "три")] },
            valid with { Question = new('?', 1001) }, valid with { Explanation = "" },
            valid with { Hints = ["a", "b"] }, Exercise(ChoiceFormat.TrueFalse) with { Options = [new(2, "False"), new(5, "True")] }
        ];
        Assert.All(invalid, exercise => Assert.Throws<ArgumentException>(exercise.Validate));
        valid.Validate();
    }

    private static ChoiceExercise Exercise(ChoiceFormat format)
    {
        ImmutableArray<ChoiceOption> options = format == ChoiceFormat.TrueFalse
            ? [new(2, "True"), new(5, "False")]
            : [new(2, "Первый вариант"), new(5, "Второй вариант"), new(4, "Третий вариант")];
        return new(1, format, "Проверьте условия задачи.", options, format == ChoiceFormat.MultipleChoice ? [2, 5] : [2],
            "Обоснование выбора.", ["Сопоставьте варианты с условием."]);
    }

    private static (Lesson Lesson, LearningService Service, KnowledgeStore Knowledge) Create(ChoiceFormat format,
        bool reused = false, bool review = false)
    {
        var user = new UserId(123);
        var knowledge = new KnowledgeStore();
        var skill = Skill.Create(user, "Тема", "Навык");
        knowledge.Add(skill);
        var question = Exercise(format);
        var lesson = Lesson.Start(user, "Тема", "Разберём условие.", question.Question);
        lesson.PublishChoice(question, reused);
        lesson.LinkToSkill(skill);
        if (review) lesson.MarkAsReview();
        return (lesson, new(new SingleLesson(lesson), new NoProvider(), new(knowledge, new FixedTimeProvider(DateTime.UnixEpoch.AddDays(10)))),
            knowledge);
    }

    private sealed class SingleLesson(Lesson lesson) : ILessonStore
    {
        public Task<Lesson?> FindActiveAsync(UserId user, CancellationToken token) => Task.FromResult<Lesson?>(
            lesson.UserId == user && lesson.Status == LessonStatus.AwaitingAnswer ? lesson : null);
        public Task<Lesson?> FindLatestAssessedAsync(UserId user, Guid? programId, CancellationToken token) => throw new NotSupportedException();
        public void Add(Lesson value) => throw new NotSupportedException();
    }

    private sealed class NoProvider : ILessonTeacher
    {
        public Task<LessonMaterial> GenerateAsync(LessonRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<LessonAssessment> AssessAsync(Lesson lesson, string answer, CancellationToken token) => throw new NotSupportedException();
        public Task<LessonExplanation> ExplainAsync(Lesson lesson, string request, LessonInteractionKind kind, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
