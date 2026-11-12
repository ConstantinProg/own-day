using OwnDay.Domain;
using OwnDay.Domain.Dialogue;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.UnitTests.Dialogue;

public sealed class DialogueContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Stamp = new('A', 64);
    private static readonly UserId Owner = new(101);

    [Fact]
    public void Synchronize_ReorderedTurn_DoesNotLowerQuestionOrInterruptionBoundary()
    {
        var lesson = Lesson.Start(Owner, "English", "Theory", "Question");
        var context = DialogueContext.Create(Owner, null, Now);
        context.Synchronize(lesson, Now, sourceOrder: 10);
        context.OpenClarification(Guid.NewGuid(), [DialogueOperationKind.Answer, DialogueOperationKind.Clarify], Stamp, Now);
        context.Synchronize(lesson, Now.AddSeconds(1), sourceOrder: 9);
        Assert.Equal(10, context.ResponseAfterSourceOrder);
        context.Interrupt(Now.AddSeconds(2), sourceOrder: 8);
        Assert.Equal(10, context.InterruptedSourceOrder);
        context.Interrupt(Now.AddSeconds(3), sourceOrder: 7);
        Assert.Equal(10, context.InterruptedSourceOrder);
    }

    [Fact]
    public void Confirm_EarlierSourceMessage_RejectsEvenWhenReceiptArrivesLater()
    {
        var context = DialogueContext.Create(Owner, null, Now);
        context.OpenConfirmation(Guid.NewGuid(), DialogueOperationKind.ChangeGoal, "Another goal", Stamp, Now, sourceOrder: 10);
        var code = context.PendingConfirmation!.Code;
        Assert.Null(context.ConsumeConfirmation(code, Now.AddSeconds(1), Stamp, Now.AddSeconds(1), sourceOrder: 9));
        Assert.NotNull(context.ConsumeConfirmation(code, Now.AddSeconds(2), Stamp, Now.AddSeconds(2), sourceOrder: 11));
    }

    [Fact]
    public void Confirm_CurrentBoundCode_ConsumesOnceWithoutChangingLesson()
    {
        var lesson = Lesson.Start(Owner, "English", "Theory", "Write a request.");
        var context = DialogueContext.Create(Owner, lesson, Now);
        context.OpenConfirmation(Guid.NewGuid(), DialogueOperationKind.Finish, "", Stamp, Now);
        var pending = context.PendingConfirmation!;
        Assert.Equal(context.Revision, pending.BaseRevision);
        Assert.Null(context.ConsumeConfirmation("да", Now.AddSeconds(1), Stamp, Now.AddSeconds(1)));
        Assert.Null(context.ConsumeConfirmation(pending.Code, Now.AddSeconds(-1), Stamp, Now.AddSeconds(1)));
        Assert.Null(context.ConsumeConfirmation(pending.Code, Now.AddSeconds(1), new('B', 64), Now.AddSeconds(1)));
        Assert.Equal(pending, context.ConsumeConfirmation(pending.Code, Now.AddSeconds(1), Stamp, Now.AddSeconds(1)));
        Assert.Null(context.ConsumeConfirmation(pending.Code, Now.AddSeconds(2), Stamp, Now.AddSeconds(2)));
        Assert.Equal(LessonStatus.AwaitingAnswer, lesson.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Confirm_ExpiredOrInterrupted_RejectsOldProposal(bool interrupt)
    {
        var context = DialogueContext.Create(Owner, null, Now);
        context.OpenConfirmation(Guid.NewGuid(), DialogueOperationKind.ChangeGoal, "new goal", Stamp, Now);
        var code = context.PendingConfirmation!.Code;
        if (interrupt) context.Interrupt(Now.AddSeconds(1));
        Assert.Null(context.ConsumeConfirmation(code, Now.AddMinutes(11), Stamp, Now.AddMinutes(11)));
    }

    [Fact]
    public void Clarify_ExplicitBoundChoice_ReturnsOriginalRequestOnce()
    {
        var request = Guid.NewGuid();
        var context = DialogueContext.Create(Owner, Lesson.Start(Owner, "English", "Theory", "Question"), Now);
        var choices = new[] { DialogueOperationKind.Answer, DialogueOperationKind.Clarify };
        context.OpenClarification(request, choices, Stamp, Now);
        choices[0] = DialogueOperationKind.Finish;
        Assert.Null(context.ConsumeClarification(DialogueOperationKind.Finish, Now.AddSeconds(1), Stamp, Now.AddSeconds(1)));
        Assert.Null(context.ConsumeClarification(DialogueOperationKind.Answer, Now.AddSeconds(-1), Stamp, Now.AddSeconds(1)));
        Assert.Equal(request, context.ConsumeClarification(DialogueOperationKind.Answer, Now.AddSeconds(1), Stamp, Now.AddSeconds(1)));
        Assert.Null(context.ConsumeClarification(DialogueOperationKind.Answer, Now.AddSeconds(2), Stamp, Now.AddSeconds(2)));
    }

    [Fact]
    public void Synchronize_PausedOrForeignLesson_PreservesAuthorityAndOwnerIsolation()
    {
        var lesson = Lesson.Start(Owner, "English", "Theory", "Question");
        var context = DialogueContext.Create(Owner, lesson, Now);
        lesson.Pause();
        context.Synchronize(lesson, Now.AddSeconds(1));
        Assert.Equal(DialoguePhase.Paused, context.Phase);
        Assert.Equal(DialogueExpectedResponse.None, context.ExpectedResponse);
        Assert.Equal(lesson.Id, context.TargetLessonId);
        Assert.Equal(LessonStatus.Paused, lesson.Status);
        Assert.Throws<ArgumentException>(() => context.Synchronize(Lesson.Start(new(202), "Other", "Theory", "Question"), Now));
        Assert.Throws<ArgumentException>(() => context.OpenConfirmation(Guid.NewGuid(), DialogueOperationKind.Answer, "", Stamp, Now));
    }
}
