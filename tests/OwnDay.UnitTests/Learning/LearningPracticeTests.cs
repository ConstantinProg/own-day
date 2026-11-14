using OwnDay.Domain;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningPracticeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Begin_OutOfBoundVolume_RejectsWithoutReplacingSelection(int count)
    {
        var program = Program(new(1));
        var context = LearningContext.Create(program);
        Assert.Throws<InvalidOperationException>(() => context.BeginPractice(count));
        Assert.Null(context.Practice);
        Assert.Equal(program.Id, context.ProgramId);
    }

    [Fact]
    public void Issue_ThreeExercises_RequiresPreviousCompletionSameSkillAndBoundedCount()
    {
        var user = new UserId(1);
        var context = LearningContext.Create(Program(user));
        var skill = Skill.Create(user, "Fractions");
        context.BeginPractice(3);
        var first = LessonFor(skill);
        context.IssuePracticeLesson(first, null);
        var second = LessonFor(skill);
        Assert.Throws<InvalidOperationException>(() => context.IssuePracticeLesson(second, first));
        Assert.Throws<InvalidOperationException>(() => context.BeginPractice(3));
        Assert.Throws<InvalidOperationException>(() => context.Select(Program(user)));
        first.Complete("Answer", "Feedback", true);
        Assert.Throws<InvalidOperationException>(() => context.IssuePracticeLesson(LessonFor(Skill.Create(user, "Other skill")), first));
        Assert.Throws<InvalidOperationException>(() => context.IssuePracticeLesson(LessonFor(Skill.Create(new(2), "Fractions")), first));
        context.IssuePracticeLesson(second, first);
        second.Complete("Answer", "Feedback", true);
        var third = LessonFor(skill);
        context.IssuePracticeLesson(third, second);
        Assert.Throws<InvalidOperationException>(() => context.CompletePractice(third));
        third.Complete("Answer", "Feedback", true);
        context.CompletePractice(third);
        Assert.Equal(LearningPracticeStatus.Completed, context.Practice!.Status);
        Assert.Equal(3, context.Practice.LessonIds.Length);
        Assert.Throws<InvalidOperationException>(() => context.IssuePracticeLesson(LessonFor(skill), third));
        Assert.Throws<InvalidOperationException>(() => context.CompletePractice(third));
        context.Select(Program(user));
    }

    [Fact]
    public void PauseResumeStop_PreservesAssociationsAndUnfinishedLesson()
    {
        var user = new UserId(1);
        var context = LearningContext.Create(Program(user));
        var lesson = LessonFor(Skill.Create(user, "Fractions"));
        context.BeginPractice(2);
        context.IssuePracticeLesson(lesson, null);
        context.PausePractice();
        context.ResumePractice();
        context.StopPractice();
        Assert.Equal(LearningPracticeStatus.Stopped, context.Practice!.Status);
        Assert.Equal(lesson.Id, Assert.Single(context.Practice.LessonIds));
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
        Assert.Null(lesson.Answer);
        Assert.Throws<InvalidOperationException>(context.ResumePractice);
    }

    private static LearningProgram Program(UserId user) => LearningProgram.Create(user, "Goal", "Beginner", null,
        "Mathematics", [new("root", "Fractions", [])], null);
    private static Lesson LessonFor(Skill skill)
    {
        var lesson = Lesson.Start(skill.UserId, skill.Title, "Theory", "Question");
        lesson.LinkToSkill(skill);
        return lesson;
    }
}
