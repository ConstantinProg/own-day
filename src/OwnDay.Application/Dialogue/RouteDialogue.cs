namespace OwnDay.Application.Dialogue;

internal static class RouteDialogue
{
    private static readonly (string Phrase, string Action)[] EditPhrases =
    [
        ("пропустить тему ", "skip"), ("skip topic ", "skip"),
        ("вернуть тему ", "unskip"), ("restore topic ", "unskip"),
        ("сократить тему ", "brief"), ("shorten topic ", "brief"),
        ("углубить тему ", "detailed"), ("go deeper on topic ", "detailed"),
        ("откатить маршрут к версии ", "restore"), ("restore route version ", "restore")
    ];

    public static string? View(string text) => text switch
    {
        "покажи маршрут" or "маршрут" or "show route" => "",
        "покажи дерево тем" or "полный маршрут" or "show full route" => "tree",
        "история маршрута" or "route history" => "history",
        _ => null
    };

    public static string? Edit(string text)
    {
        foreach (var (phrase, action) in EditPhrases)
        {
            if (text.StartsWith(phrase, StringComparison.Ordinal))
                return action + " " + text[phrase.Length..].Trim();
        }

        foreach (var (prefix, separator) in new[] { ("перенеси тему ", " перед "), ("move topic ", " before ") })
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var topics = text[prefix.Length..].Split(separator, 2, StringSplitOptions.TrimEntries);
            if (topics is [var moving, var before]) return $"before {moving} {before}";
        }
        return null;
    }

    public static string? DescribeEdit(string text)
    {
        if (text.Length > 100) return null;
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) switch
        {
            ["skip", var topic] => $"Пропустить тему {topic}? Следующие темы смогут опираться на этот пропуск. Зачёт за тему можно получить в отдельном упражнении.",
            ["unskip", var topic] => $"Вернуть тему {topic} в маршрут?",
            ["brief", var topic] => $"Сократить будущие упражнения по теме {topic}?",
            ["detailed", var topic] => $"Углубить будущие упражнения по теме {topic}?",
            ["standard", var topic] => $"Вернуть обычную глубину темы {topic}?",
            ["before", var topic, var before] => $"Переместить тему {topic} перед темой {before}?",
            ["restore", var version] => $"Восстановить маршрут версии {version} в новой версии? История упражнений и оценок сохранится.",
            _ => null
        };
    }
}
