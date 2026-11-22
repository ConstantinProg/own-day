namespace OwnDay.Infrastructure.Telegram.Delivery;

public interface ITelegramMessageSender
{
    Task SendMessageAsync(
        long chatId,
        string text,
        string? replyMarkupJson,
        CancellationToken cancellationToken = default);

    Task EditKeyboardAsync(long chatId, int messageId, string? replyMarkupJson, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("This sender does not support keyboard edits."));

    Task SendDocumentAsync(long chatId, Stream content, string name, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("This sender does not support documents."));
}
