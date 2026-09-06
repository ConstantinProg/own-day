namespace OwnDay.Infrastructure.Telegram.Commands;

public readonly record struct TelegramCommandParseResult
{
    private TelegramCommandParseResult(TelegramCommand? command)
    {
        Command = command;
    }

    public bool IsCommand => Command is not null;

    public TelegramCommand? Command { get; }

    public static TelegramCommandParseResult NotCommand { get; } = new(null);

    public static TelegramCommandParseResult Success(TelegramCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new TelegramCommandParseResult(command);
    }
}
