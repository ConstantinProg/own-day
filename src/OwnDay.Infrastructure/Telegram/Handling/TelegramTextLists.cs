using System.Text;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Handling;

public static class TelegramTextLists
{
    private const int Limit = 4096;

    public static IReadOnlyList<string> Render<T>(string heading, IReadOnlyList<T> items, Func<T, string> format, string locale) =>
        items.Count == 0
            ? [TelegramTexts.Get(locale, "list.empty", heading)]
            : Split(items.Select(format), heading);

    public static IReadOnlyList<string> Split(IEnumerable<string> lines, string heading = "")
    {
        if (heading.Length >= Limit - 1)
        {
            throw new ArgumentException("Heading must leave room for list items.", nameof(heading));
        }

        var messages = new List<string>();
        var current = new StringBuilder(heading);
        var maximumLineLength = Limit - (heading.Length == 0 ? 0 : heading.Length + 1);
        foreach (var value in lines)
        {
            var line = value.Length <= maximumLineLength
                ? value
                : value[..(maximumLineLength - 1)] + "…";
            if (current.Length + (current.Length == 0 ? 0 : 1) + line.Length > Limit)
            {
                messages.Add(current.ToString());
                current.Clear().Append(heading);
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
        }

        if (current.Length > 0)
        {
            messages.Add(current.ToString());
        }

        return messages;
    }
}
