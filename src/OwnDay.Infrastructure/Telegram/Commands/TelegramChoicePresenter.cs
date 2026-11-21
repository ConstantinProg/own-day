using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;

namespace OwnDay.Infrastructure.Telegram.Commands;

internal static class TelegramChoicePresenter
{
    public static TelegramLearningReply Render(ChoicePrompt prompt)
    {
        if (prompt.AfterSolution is { } next)
        {
            var invitation = (next, prompt.InRussian) switch
            {
                (LearningContinuation.NextTopic, true) => "Для проверки на новом примере напишите «дальше».",
                (LearningContinuation.NextTopic, false) => "Write “next” to check your understanding on a new example.",
                (_, true) => "Для проверки на новом примере напишите «ещё задание».",
                _ => "Write “another exercise” to check your understanding on a new example."
            };
            return TelegramLearningReply.WithActions(invitation, LearningActionProvider.ForChoice(prompt), prompt.InRussian);
        }
        var labels = prompt.InRussian ? ChoicePrompt.CyrillicLabels : ChoicePrompt.LatinLabels;
        var lines = prompt.Options.Select((option, index) =>
            $"{labels[index]}. {LearningService.ChoiceLabel(option, prompt.Format, prompt.InRussian)}");
        var instructions = (prompt.InRussian, prompt.Format == ChoiceFormat.MultipleChoice) switch
        {
            (true, true) => "Выберите варианты и нажмите «Ответить». Текстом — буквы через запятую.",
            (true, false) => "Текстом можно отправить букву варианта.",
            (false, true) => "Choose options, then press Submit. To answer by text, send the letters separated by commas.",
            _ => "You can also answer with the option letter."
        };
        return new(prompt.Question + "\n\n" + string.Join('\n', lines) + "\n\n" + instructions)
        {
            Actions = LearningActionProvider.ForChoice(prompt),
            Choice = prompt,
            InRussian = prompt.InRussian
        };
    }
}
