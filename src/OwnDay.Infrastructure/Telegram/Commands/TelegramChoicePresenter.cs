using System.Globalization;
using System.Text.Json;
using OwnDay.Application.Learning;
using OwnDay.Domain.Learning;
using Telegram.Bot.Types.ReplyMarkups;

namespace OwnDay.Infrastructure.Telegram.Commands;

internal static class TelegramChoicePresenter
{
    private const string Prefix = "lc";

    public static TelegramLearningReply Render(ChoicePrompt prompt) => new(Text(prompt), Markup(prompt));

    private static string Text(ChoicePrompt prompt)
    {
        var russian = prompt.InRussian;
        var multiple = prompt.Format == ChoiceFormat.MultipleChoice;
        var labels = russian ? ChoicePrompt.CyrillicLabels : ChoicePrompt.LatinLabels;
        var lines = prompt.Options.Select((option, index) =>
            $"{labels[index]}. {LearningService.ChoiceLabel(option, prompt.Format, russian)}");
        var instructions = (russian, multiple) switch
        {
            (true, true) => "Выберите все подходящие варианты и нажмите «Ответить». Можно написать буквы через запятую.",
            (true, false) => "Нажмите вариант или напишите его букву.",
            (false, true) => "Select all that apply, then press Submit. You can also type the letters separated by commas.",
            _ => "Press an option or type its letter."
        };
        var selection = multiple
            ? "\n\n" + (russian ? "Выбрано — " : "Selected — ") + (prompt.Draft.IsEmpty
                ? russian ? "ничего" : "none"
                : string.Join(", ", prompt.Options.Select((option, index) => (option.Id, Label: labels[index]))
                    .Where(option => prompt.Draft.Contains(option.Id)).Select(option => option.Label)))
            : "";
        return prompt.Question + "\n\n" + string.Join('\n', lines) + selection + "\n\n" + instructions;
    }

    private static string Markup(ChoicePrompt prompt)
    {
        var russian = prompt.InRussian;
        var multiple = prompt.Format == ChoiceFormat.MultipleChoice;
        var labels = russian ? ChoicePrompt.CyrillicLabels : ChoicePrompt.LatinLabels;
        var rows = prompt.Options.Select((option, index) =>
        {
            var text = $"{labels[index]}. {LearningService.ChoiceLabel(option, prompt.Format, russian)}";
            if (multiple)
                text += prompt.Draft.Contains(option.Id) ? russian ? " — выбрано" : " — selected" : russian ? " — не выбрано" : " — not selected";
            return new[] { InlineKeyboardButton.WithCallbackData(text, Encode(prompt, multiple ? 't' : 'a', option.Id)) };
        }).ToList();
        if (multiple && !prompt.Draft.IsEmpty)
            rows.Add([InlineKeyboardButton.WithCallbackData(russian ? "Ответить" : "Submit", Encode(prompt, 's', 0))]);
        return JsonSerializer.Serialize(new InlineKeyboardMarkup(rows));
    }

    private static string Encode(ChoicePrompt prompt, char action, int option) =>
        FormattableString.Invariant($"{Prefix}:{prompt.LessonId:N}:{prompt.Version}:{prompt.Revision}:{action}:{option}");

    public static ChoiceInput? Parse(string data)
    {
        if (data.Length > 64 || !data.StartsWith(Prefix + ":", StringComparison.Ordinal)) return null;
        var parts = data.Split(':');
        if (parts.Length != 6 || !Guid.TryParseExact(parts[1], "N", out var lessonId) || lessonId == Guid.Empty ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1 ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1 ||
            !int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var option))
            return null;
        return parts[4] switch
        {
            "a" when option is >= 1 and <= ChoiceExercise.MaxOptions => new(lessonId, version, revision, ChoiceAction.Select, option),
            "t" when option is >= 1 and <= ChoiceExercise.MaxOptions => new(lessonId, version, revision, ChoiceAction.Toggle, option),
            "s" when option == 0 => new(lessonId, version, revision, ChoiceAction.Submit),
            _ => null
        };
    }
}
