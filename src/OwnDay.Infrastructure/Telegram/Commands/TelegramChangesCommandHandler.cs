using OwnDay.Infrastructure.Changelog;
using OwnDay.Infrastructure.Telegram.Delivery;
using OwnDay.Infrastructure.Telegram.Localization;

namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramChangesCommandHandler(ApplicationVersion version, ITelegramMessageSender sender)
{
    private const int MessageLimit = 4096;
    private const string HistoryUrl = "https://github.com/ConstantinProg/own-day/blob/main/CHANGELOG.md";

    public async Task HandleAsync(long chatId, string locale, CancellationToken cancellationToken)
    {
        var notes = await ReleaseChangelog.ReadNotesAsync(version.Version, locale, cancellationToken);
        await sender.SendMessageAsync(chatId, Render(version.Version, notes, locale), null, cancellationToken);
    }

    public static string Render(string version, string? notes, string locale)
    {
        var heading = $"OwnDay {version}\n\n";
        var footer = $"\n\n{TelegramTexts.Get(locale, "changes.history")}\n{HistoryUrl}";
        var body = notes ?? TelegramTexts.Get(locale, "changes.missing");
        var available = MessageLimit - heading.Length - footer.Length;
        if (body.Length > available)
        {
            var notice = $"…\n\n{TelegramTexts.Get(locale, "changes.truncated")}";
            var cutoff = available - notice.Length;
            if (char.IsHighSurrogate(body[cutoff - 1]))
            {
                cutoff--;
            }
            body = body[..cutoff].TrimEnd() + notice;
        }

        return heading + body + footer;
    }
}
