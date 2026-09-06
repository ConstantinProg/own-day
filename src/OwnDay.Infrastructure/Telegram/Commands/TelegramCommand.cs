namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed record TelegramCommand(
    string Name,
    string? BotUsername,
    string Arguments);
