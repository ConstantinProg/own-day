using System.Text;

namespace OwnDay.Infrastructure.Telegram.Handling;

public static class TelegramTextLists
{
    private const int Limit = 4096;

    public static IReadOnlyList<string> Render<T>(string heading, IReadOnlyList<T> items, Func<T, string> format)
    {
        if (items.Count == 0)
        {
            return [$"{heading}: пусто."];
        }

        var messages = new List<string>();
        var current = new StringBuilder(heading);
        foreach (var item in items)
        {
            var line = format(item);
            if (line.Length > Limit - 32)
            {
                line = line[..(Limit - 33)] + "…";
            }

            if (current.Length + line.Length + 1 > Limit)
            {
                messages.Add(current.ToString());
                current.Clear();
                current.Append(heading);
            }

            current.Append('\n').Append(line);
        }

        messages.Add(current.ToString());
        return messages;
    }
}
