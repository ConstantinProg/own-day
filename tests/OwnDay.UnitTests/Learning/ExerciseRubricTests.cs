using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class ExerciseRubricTests
{
    [Fact]
    public void ApplyRubricAssessment_RepeatedWrongAnswers_BoundsHelpAndRetainsTheOriginalAttempts()
    {
        var lesson = Lesson.Start(new UserId(1), "Types", "Theory", "Compare these types.",
            content: Content(ExerciseEvidenceKind.Understanding));
        var incorrect = new RubricAssessment([new("meaning", RubricCriterionResult.NotMet, "A required relation is missing.")]);
        var operation = Guid.NewGuid();
        lesson.ApplyRubricAssessment("first wrong answer", "Feedback", incorrect, operation, new(ExerciseCorrectionKind.Example, "Try a smaller example."));
        lesson.ApplyRubricAssessment("second wrong answer", "Feedback", incorrect, Guid.NewGuid());

        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        Assert.Equal(AnswerAssistance.Assisted, lesson.Assistance);
        Assert.False(lesson.CanOfferMoreHelp);
        Assert.Equal(2, lesson.CorrectionCount);
        Assert.Equal(operation, lesson.RubricEvaluation!.Corrections[0].OperationId);
        Assert.Throws<InvalidOperationException>(() => lesson.AddExplanation(LessonInteractionKind.Clarification, "Hint", "Help"));

        lesson.ApplyRubricAssessment("third wrong answer", "Feedback", incorrect, Guid.NewGuid());
        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.False(lesson.IsCorrect);
        Assert.Equal("first wrong answer", lesson.RubricEvaluation!.Corrections[0].Answer);
        Assert.Equal("third wrong answer", lesson.Answer);
    }

    [Fact]
    public void ApplyRubricAssessment_UnclearThenUnclear_PreservesBothResponsesWithoutCredit()
    {
        var owner = new UserId(1);
        var lesson = Lesson.Start(owner, "Unions", "Theory", "Compare these types.", content: Content(ExerciseEvidenceKind.Understanding));
        var operation = Guid.NewGuid();
        var followUpOperation = Guid.NewGuid();
        var unknown = new RubricAssessment([new("meaning", RubricCriterionResult.Unclear, "The relation is unclear.")]);
        Assert.Throws<ArgumentException>(() => lesson.ApplyRubricAssessment("first", "Feedback",
            new([new("different", RubricCriterionResult.Met, "Wrong rubric")]), operation));
        Assert.Null(lesson.RubricEvaluation);

        lesson.ApplyRubricAssessment("first", "Feedback", unknown, operation);
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        Assert.NotEqual(lesson.Question, lesson.ResponseQuestion);
        Assert.Null(lesson.IsCorrect);
        lesson.ApplyRubricAssessment("follow-up", "Still unclear.", unknown, followUpOperation);

        Assert.Equal(LessonStatus.Completed, lesson.Status);
        Assert.Equal(AnswerAssessmentStatus.Inconclusive, lesson.AssessmentStatus);
        Assert.Equal("first", lesson.RubricEvaluation!.InitialAnswer);
        Assert.Equal(operation, lesson.RubricEvaluation.InitialOperationId);
        Assert.Equal(followUpOperation, lesson.RubricEvaluation.OperationId);
        Assert.Equal("follow-up", lesson.Answer);
        Assert.False(lesson.IsCorrect);
    }

    [Fact]
    public void Evaluate_IndependentEvidence_RequiresDistinctUnderstandingAppliedWorkAndDelayedReview()
    {
        var skill = Skill.Create(new UserId(1), "Types");
        var start = DateTimeOffset.UnixEpoch;
        var first = Record(skill, "Question one", "Answer one", ExerciseEvidenceKind.Understanding, start);
        var repeated = Record(skill, " question  one ", "Another answer", ExerciseEvidenceKind.Understanding, start.AddMinutes(1));
        var repeatedAnswer = Record(skill, "Question two", " Answer   one ", ExerciseEvidenceKind.Understanding, start.AddMinutes(2));
        var second = Record(skill, "Question two", "Answer two", ExerciseEvidenceKind.Understanding, start.AddMinutes(3));
        var assisted = Record(skill, "Apply", "Application", ExerciseEvidenceKind.Application, start.AddMinutes(4), assisted: true);
        var application = Record(skill, "Apply independently", "Independent application", ExerciseEvidenceKind.Application, start.AddMinutes(5));
        var immediate = Record(skill, "Review now", "Immediate retrieval", ExerciseEvidenceKind.Retrieval, start.AddHours(23));
        var delayed = Record(skill, "Review later", "Delayed retrieval", ExerciseEvidenceKind.Retrieval, start.AddDays(2));

        Assert.Equal(SkillMastery.Developing, LearnerSkillState.Evaluate([first]).Mastery);
        Assert.False(LearnerSkillState.Evaluate([first, repeated, repeatedAnswer]).EvidenceSummary!.Understanding);
        Assert.False(LearnerSkillState.Evaluate([first, second, assisted]).EvidenceSummary!.Application);
        Assert.False(LearnerSkillState.Evaluate([first, second, application, immediate]).EvidenceSummary!.Retention);
        Assert.Equal(SkillMastery.Demonstrated, LearnerSkillState.Evaluate([first, second, application, delayed]).Mastery);
    }

    private static LearningContent Content(ExerciseEvidenceKind kind) => new("types", "rubric-v1", LearningContentKind.StructuredExercise,
        "Understand type relations", "Describe the relation", "All published criteria must be met.",
        new(1, kind, [new("meaning", "Describe the type relation in your own words.")]));

    private static LearningEvidence Record(Skill skill, string question, string answer, ExerciseEvidenceKind kind,
        DateTimeOffset at, bool assisted = false)
    {
        var lesson = Lesson.Start(skill.UserId, skill.Title, "Theory", question, content: Content(kind));
        lesson.LinkToSkill(skill);
        if (kind == ExerciseEvidenceKind.Retrieval) lesson.MarkAsReview();
        if (assisted) lesson.AddExplanation(LessonInteractionKind.Clarification, "Help", "Explanation");
        lesson.ApplyRubricAssessment(answer, "Feedback", new([new("meaning", RubricCriterionResult.Met, "Explained")]), Guid.NewGuid());
        return LearningEvidence.FromAnswer(lesson, at, "test");
    }
}
