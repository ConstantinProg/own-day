namespace OwnDay.Infrastructure.Telegram.Commands;

public sealed class TelegramCommandParser
{
    public TelegramCommandParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TelegramCommandParseResult.NotCommand;
        }

        var span = text.AsSpan().Trim();

        if (span.Length < 2 || span[0] != '/')
        {
            return TelegramCommandParseResult.NotCommand;
        }

        var separatorIndex = FindWhitespace(span);

        var commandToken = separatorIndex >= 0
            ? span[..separatorIndex]
            : span;

        var arguments = separatorIndex >= 0
            ? span[separatorIndex..].Trim()
            : [];

        if (!TryParseCommandToken(commandToken, out var commandName, out var botUsername))
        {
            return TelegramCommandParseResult.NotCommand;
        }

        return TelegramCommandParseResult.Success(
            new TelegramCommand(
                commandName,
                botUsername,
                arguments.ToString()));
    }

    private static bool TryParseCommandToken(
        ReadOnlySpan<char> token,
        out string commandName,
        out string? botUsername)
    {
        commandName = string.Empty;
        botUsername = null;

        var command = token[1..];

        if (command.IsEmpty)
        {
            return false;
        }

        var atIndex = command.IndexOf('@');

        ReadOnlySpan<char> name;

        if (atIndex < 0)
        {
            name = command;
        }
        else
        {
            name = command[..atIndex];
            var username = command[(atIndex + 1)..];

            if (username.IsEmpty || username.Contains('@'))
            {
                return false;
            }

            botUsername = username.ToString();
        }

        if (name.IsEmpty)
        {
            return false;
        }

        if (!IsValidCommandName(name))
        {
            return false;
        }

        if (botUsername is not null && !IsValidBotUsername(botUsername))
        {
            return false;
        }

        commandName = name.ToString();

        return true;
    }

    private static int FindWhitespace(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsValidCommandName(ReadOnlySpan<char> commandName) =>
        ContainsOnlyIdentifierCharacters(commandName);

    private static bool IsValidBotUsername(ReadOnlySpan<char> botUsername) =>
        ContainsOnlyIdentifierCharacters(botUsername);

    private static bool ContainsOnlyIdentifierCharacters(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}
