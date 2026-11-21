using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class LearningActionProviderTests
{
    [Fact]
    public void CanOffer_TheoryPending_RequiresNextActionTargetingTheCurrentLesson()
    {
        var user = new UserId(1);
        var program = LearningProgram.Create(user, "Learn fractions", "Beginner", null, "Math",
            [new("fractions", "Fractions", [])], null);
        var lesson = Lesson.Start(user, "Fractions", "A fraction expresses some parts of a whole.", "What is one half?");
        lesson.LinkToProgram(program, "fractions");
        lesson.PresentTheoryFirst();

        Assert.False(LearningActionProvider.CanOffer(new(LearningActionKind.Next), lesson));
        Assert.False(LearningActionProvider.CanOffer(new(LearningActionKind.Next, Guid.NewGuid()), lesson));
        Assert.True(LearningActionProvider.CanOffer(new(LearningActionKind.Next, lesson.Id), lesson));
        Assert.True(LearningActionProvider.CanOffer(new(LearningActionKind.Next), null));
    }

    [Fact]
    public void ForChoice_MultipleChoice_OffersSubmitOnlyWithSelections()
    {
        var prompt = new ChoicePrompt(Guid.NewGuid(), 1, 2, ChoiceFormat.MultipleChoice, "Select",
            [new(1, "First"), new(2, "Second")], [], InRussian: false);

        var empty = LearningActionProvider.ForChoice(prompt);
        var selected = LearningActionProvider.ForChoice(prompt with { Draft = [1] });

        Assert.Equal(2, empty.Count(action => action.Kind == LearningActionKind.ToggleChoice));
        Assert.DoesNotContain(empty, action => action.Kind == LearningActionKind.SubmitChoice);
        Assert.Equal(new ChoiceInput(prompt.LessonId, 1, 2, ChoiceAction.Submit),
            Assert.Single(selected, action => action.Kind == LearningActionKind.SubmitChoice).ToChoiceInput());
    }

    [Fact]
    public void MatchesDialogue_ReplacedConfirmation_RejectsPreviousProposal()
    {
        var now = DateTimeOffset.UtcNow;
        var context = DialogueContext.Create(new UserId(10), null, now);
        var stamp = new string('a', 64);
        context.OpenConfirmation(Guid.NewGuid(), DialogueOperationKind.SelectProgram, Guid.NewGuid().ToString(), stamp, now);
        var original = new LearningAction(LearningActionKind.Reject, context.PendingConfirmation!.ProposalId, context.Revision);

        Assert.True(LearningActionProvider.MatchesDialogue(original, context));
        context.OpenConfirmation(Guid.NewGuid(), DialogueOperationKind.SelectProgram, Guid.NewGuid().ToString(), stamp, now);

        Assert.False(LearningActionProvider.MatchesDialogue(original, context));
        Assert.False(LearningActionProvider.MatchesDialogue(original with { Revision = context.Revision }, context));
    }

    [Fact]
    public void ForSchedule_PauseAndOptOut_OffersOnlyAvailableActions()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = StudySchedule.Create(new UserId(10), "UTC", 1, [new(new(10, 0), new(11, 0))], now, StudyReminderType.Study);
        Assert.Contains(StudyActionProvider.ForSchedule(schedule), action => action.Kind == LearningActionKind.PauseReminders);

        schedule.SetPaused(true, now);
        Assert.Contains(StudyActionProvider.ForSchedule(schedule), action => action.Kind == LearningActionKind.ResumeReminders);
        Assert.DoesNotContain(StudyActionProvider.ForSchedule(schedule), action => action.Kind == LearningActionKind.PauseReminders);

        schedule.OptOut();
        Assert.Empty(StudyActionProvider.ForSchedule(schedule));
    }
}
