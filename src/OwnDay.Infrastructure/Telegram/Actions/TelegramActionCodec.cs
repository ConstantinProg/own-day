using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OwnDay.Application.Learning;
using OwnDay.Application.TesterMode;
using OwnDay.Infrastructure.Telegram.Configuration;

namespace OwnDay.Infrastructure.Telegram.Actions;

public sealed class TelegramActionCodec
{
    private const string Prefix = "a1.";
    private const int PayloadSize = 30;
    private const int TagSize = 12;
    private const int TesterKindOffset = 128;
    private const int EncodedSize = (PayloadSize + TagSize) / 3 * 4;
    private readonly byte[] _key;

    public TelegramActionCodec(IOptions<TelegramOptions> options)
    {
        var settings = options.Value;
        var configuredKey = Convert.FromBase64String(settings.CallbackSigningKey!);
        var botId = settings.BotToken!.Split(':', 2)[0];
        _key = HMACSHA256.HashData(configuredKey, Encoding.UTF8.GetBytes($"OwnDay/Telegram/contextual-actions/{botId}"));
        CryptographicOperations.ZeroMemory(configuredKey);
    }

    public string Encode(LearningAction action, long owner, long chat, DateTimeOffset expiresAt)
    {
        if (!IsValid(action) || owner <= 0 || chat != owner) throw new ArgumentException("Invalid contextual action.");
        Span<byte> bytes = stackalloc byte[PayloadSize + TagSize];
        bytes[0] = (byte)action.Kind;
        if (HasNumberTarget(action.Kind))
        {
            BinaryPrimitives.WriteInt64BigEndian(bytes[1..9], action.Number);
            BinaryPrimitives.WriteInt32BigEndian(bytes[9..13], action.RouteVersion);
            BinaryPrimitives.WriteInt32BigEndian(bytes[13..17], action.TopicIndex);
        }
        else action.TargetId.TryWriteBytes(bytes[1..17]);
        if (action.Kind <= LearningActionKind.SubmitChoice)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes[17..21], action.ChoiceVersion);
            BinaryPrimitives.WriteInt32BigEndian(bytes[21..25], checked((int)action.Revision));
        }
        else BinaryPrimitives.WriteInt64BigEndian(bytes[17..25], action.Revision);
        bytes[25] = action.Option;
        return Sign(bytes, owner, chat, expiresAt);
    }

    public bool TryDecode(string data, long owner, long chat, out LearningAction? action, out DateTimeOffset expiresAt)
    {
        action = null;
        Span<byte> bytes = stackalloc byte[PayloadSize + TagSize];
        if (!TryReadSigned(data, owner, chat, bytes, out expiresAt)) return false;

        var kind = (LearningActionKind)bytes[0];
        var decoded = kind switch
        {
            <= LearningActionKind.SubmitChoice => new LearningAction(kind, new Guid(bytes[1..17]),
                BinaryPrimitives.ReadInt32BigEndian(bytes[21..25]), bytes[25]) { ChoiceVersion = BinaryPrimitives.ReadInt32BigEndian(bytes[17..21]) },
            _ when HasNumberTarget(kind) => new LearningAction(kind, Revision: BinaryPrimitives.ReadInt64BigEndian(bytes[17..25]), Option: bytes[25])
            {
                Number = BinaryPrimitives.ReadInt64BigEndian(bytes[1..9]),
                RouteVersion = BinaryPrimitives.ReadInt32BigEndian(bytes[9..13]),
                TopicIndex = BinaryPrimitives.ReadInt32BigEndian(bytes[13..17])
            },
            _ => new LearningAction(kind, new Guid(bytes[1..17]), BinaryPrimitives.ReadInt64BigEndian(bytes[17..25]), bytes[25])
        };
        if (!IsValid(decoded)) return false;
        action = decoded;
        return true;
    }

    public string Encode(TesterModeConfirmation confirmation, long owner, long chat)
    {
        if (owner <= 0 || chat != owner || confirmation.Id == Guid.Empty || !Enum.IsDefined(confirmation.Operation))
            throw new ArgumentException("Invalid Tester Mode action.", nameof(confirmation));
        Span<byte> bytes = stackalloc byte[PayloadSize + TagSize];
        bytes.Clear();
        bytes[0] = checked((byte)(TesterKindOffset + (int)confirmation.Operation));
        confirmation.Id.TryWriteBytes(bytes[1..17]);
        return Sign(bytes, owner, chat, confirmation.ExpiresAt);
    }

    public bool TryDecodeTester(string data, long owner, long chat, out TesterModeConfirmation? confirmation)
    {
        confirmation = null;
        Span<byte> bytes = stackalloc byte[PayloadSize + TagSize];
        if (!TryReadSigned(data, owner, chat, bytes, out var expiresAt)) return false;
        var operation = (TesterModeOperation)(bytes[0] - TesterKindOffset);
        var id = new Guid(bytes[1..17]);
        if (!Enum.IsDefined(operation) || id == Guid.Empty) return false;
        confirmation = new(id, operation, expiresAt);
        return true;
    }

    private string Sign(Span<byte> bytes, long owner, long chat, DateTimeOffset expiresAt)
    {
        BinaryPrimitives.WriteUInt32BigEndian(bytes[26..30], checked((uint)expiresAt.ToUnixTimeSeconds()));
        WriteTag(bytes[..PayloadSize], owner, chat, bytes[PayloadSize..]);
        return Prefix + Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    }

    private bool TryReadSigned(string data, long owner, long chat, Span<byte> bytes, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (owner <= 0 || chat != owner || data.Length != Prefix.Length + EncodedSize ||
            !data.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        Span<char> base64 = stackalloc char[EncodedSize];
        for (var index = 0; index < base64.Length; index++)
        {
            var character = data[index + Prefix.Length];
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) return false;
            base64[index] = character switch { '-' => '+', '_' => '/', _ => character };
        }
        if (!Convert.TryFromBase64Chars(base64, bytes, out var written) || written != bytes.Length) return false;
        Span<byte> tag = stackalloc byte[TagSize];
        WriteTag(bytes[..PayloadSize], owner, chat, tag);
        if (!CryptographicOperations.FixedTimeEquals(tag, bytes[PayloadSize..])) return false;
        expiresAt = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(bytes[26..30]));
        return true;
    }

    private static bool IsValid(LearningAction action) =>
        Enum.IsDefined(action.Kind) && (action.Kind <= LearningActionKind.SubmitChoice
            ? action.ToChoiceInput() is not null : action.Option == 0 && (action.Kind switch
            {
                LearningActionKind.ProjectPractice => action.Number > 0 && action.RouteVersion > 0 && action.TopicIndex >= 0,
                LearningActionKind.FocusTopic => action.Number == 0 && action.RouteVersion > 0 && action.TopicIndex >= 0,
                _ when HasNumberTarget(action.Kind) => action.Number is > 0 and <= int.MaxValue && action.RouteVersion == 0 && action.TopicIndex == 0,
                _ => true
            }));

    private static bool HasNumberTarget(LearningActionKind kind) => kind is LearningActionKind.ProjectReportPage or
        LearningActionKind.ProjectPractice or LearningActionKind.FocusTopic or LearningActionKind.RouteVersion or
        LearningActionKind.ProgramsPage or LearningActionKind.ReviewsPage;

    private void WriteTag(ReadOnlySpan<byte> payload, long owner, long chat, Span<byte> tag)
    {
        Span<byte> input = stackalloc byte[PayloadSize + 16];
        payload.CopyTo(input);
        BinaryPrimitives.WriteInt64BigEndian(input[PayloadSize..], owner);
        BinaryPrimitives.WriteInt64BigEndian(input[(PayloadSize + 8)..], chat);
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(_key, input, hash);
        hash[..TagSize].CopyTo(tag);
    }
}
