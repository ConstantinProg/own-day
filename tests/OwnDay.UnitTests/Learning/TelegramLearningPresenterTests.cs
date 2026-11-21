using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Telegram.Commands;
using Xunit;

namespace OwnDay.UnitTests.Learning;

public sealed class TelegramLearningPresenterTests
{
    [Fact]
    public void Render_ChoicePromptAndPlainReplyWithSameText_RendersOnceAndRequiresPromptForChoices()
    {
        var prompt = new ChoicePrompt(Guid.NewGuid(), 1, 1, ChoiceFormat.SingleChoice, "Two plus two?",
            [new(1, "Four"), new(2, "Five")], [], InRussian: false);
        var question = Assert.Single(TelegramLearningPresenter.Render(new LearningResult.ChoiceReply("", prompt)));

        var plain = Assert.Single(TelegramLearningPresenter.Render(new LearningResult.ChoiceReply(question.Text)));
        var merged = Assert.Single(TelegramLearningPresenter.Render(new LearningResult.ChoiceReply(question.Text, prompt)));

        Assert.Equal(question.Text, plain.Text);
        Assert.Null(plain.Actions);
        Assert.Equal(question.Text, merged.Text);
        Assert.Equal(new[] { 1, 2 }, merged.Actions!.Where(action => action.Kind == LearningActionKind.SelectChoice)
            .Select(action => (int)action.Option));
    }

    [Fact]
    public void Render_EnglishResumeWithMaximumSavedHelp_KeepsQuestionLastWithinTelegramLimit()
    {
        var session = new LearningResult.Session(new string('t', 500), new string('q', 1000), LessonStatus.AwaitingAnswer)
        {
            Content = OwnDay.Application.Learning.EnglishLearningContent.FirstLesson,
            Theory = new string('а', 2500), LastExplanation = new string('h', 2500)
        };
        var messages = TelegramLearningPresenter.Render(session);
        Assert.Contains(messages, message => message.Text.Contains("Ранее", StringComparison.Ordinal) && message.Text.Contains(session.LastExplanation, StringComparison.Ordinal));
        Assert.Contains(session.Question, messages[^1].Text);
        Assert.Contains("помощь", messages[^1].Text);
        Assert.All(messages, message => Assert.InRange(message.Text.Length, 1, 4096));
    }

    [Theory]
    [InlineData(MaterialSourceStatus.Unavailable, "Source unavailable")]
    [InlineData(MaterialSourceStatus.Retrieved, "no matching excerpt")]
    [InlineData(MaterialSourceStatus.QuoteMatched, "Only the excerpt")]
    public void Render_ProvenanceAndReferenceCheck_ShowsVerificationLimitsWithinTelegramSize(MaterialSourceStatus status, string expected)
    {
        var source = new LessonProvenance(new string('g', 200), status == MaterialSourceStatus.None ? null : new string('r', 500),
            status == MaterialSourceStatus.None ? null : new string('v', 100),
            status is MaterialSourceStatus.Retrieved or MaterialSourceStatus.QuoteMatched ? DateTimeOffset.UnixEpoch : null,
            status == MaterialSourceStatus.QuoteMatched ? new string('q', 500) : null, status);
        source.Validate();
        var messages = TelegramLearningPresenter.Render(new LearningResult.Material(new string('t', 2500), new string('q', 1000), new string('n', 500))
        {
            Provenance = source, HasReferenceCheck = true
        });
        Assert.Contains(messages, m => m.Text.Contains(expected, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Text.Contains(source.Generator, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Text.Contains("reference was proposed by AI", StringComparison.Ordinal));
        Assert.All(messages, m => Assert.InRange(m.Text.Length, 1, 4096));
    }

    [Fact]
    public void Render_StableRussianMaterial_KeepsTaskVisibleWithoutAuditDetails()
    {
        const string question = "Назовите ограничение цены.\nПриведите пример заявки.";
        var messages = TelegramLearningPresenter.Render(new LearningResult.Material("Торги сводят заявки.", question, "Биржа")
        {
            HasReferenceCheck = true,
            Provenance = new("OpenAI:test-model", null, null, null, null, MaterialSourceStatus.None)
        });

        Assert.Equal(2, messages.Count);
        Assert.Contains(question, messages[^1].Text);
        Assert.Contains("подсказку", messages[^1].Text);
        Assert.DoesNotContain(messages, text => text.Text.Contains("OpenAI", StringComparison.Ordinal) ||
            text.Text.Contains("не проверен", StringComparison.OrdinalIgnoreCase) || text.Text.Contains("/answer", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ActiveSession_ShowsCurrentQuestionAndAnswerAction()
    {
        var reply = Assert.Single(TelegramLearningPresenter.Render(
            new LearningResult.Session("Algebra", "Two plus two?", LessonStatus.AwaitingAnswer)));

        Assert.Contains("Algebra", reply.Text);
        Assert.Contains("Two plus two?", reply.Text);
        Assert.Contains("Write your answer", reply.Text);
        Assert.DoesNotContain("/answer", reply.Text);
        Assert.DoesNotContain("Lesson paused", reply.Text);
    }

    [Fact]
    public void Render_PausedSession_OffersResumeWithoutAnswerAction()
    {
        var reply = Assert.Single(TelegramLearningPresenter.Render(
            new LearningResult.Session("Algebra", "Two plus two?", LessonStatus.Paused)));

        Assert.Contains("Algebra", reply.Text);
        Assert.Contains("Two plus two?", reply.Text);
        Assert.Contains("Write “continue”", reply.Text);
        Assert.DoesNotContain("/answer", reply.Text);
    }

    [Fact]
    public void Render_MaximumMaterialAndExplanation_SplitsReadableMessagesWithinTelegramLimit()
    {
        var topic = new string('t', 500);
        var question = new string('q', 1000);
        var theory = new string('a', 2500);
        var material = TelegramLearningPresenter.Render(new LearningResult.Material(theory, question, topic));
        var explanation = TelegramLearningPresenter.Render(new LearningResult.Explanation(theory, topic, question));
        var session = TelegramLearningPresenter.Render(new LearningResult.Session(topic, question, LessonStatus.AwaitingAnswer));
        var paused = TelegramLearningPresenter.Render(new LearningResult.Session(topic, question, LessonStatus.Paused));
        Assert.Equal(2, material.Count);
        Assert.Equal(2, explanation.Count);
        Assert.All(material.Concat(explanation).Concat(session).Concat(paused), text => Assert.InRange(text.Text.Length, 1, 4096));
        Assert.Contains(topic, material[0].Text);
        Assert.Contains("Write your answer", material[1].Text);
        Assert.Contains(question, explanation[1].Text);
    }
}
