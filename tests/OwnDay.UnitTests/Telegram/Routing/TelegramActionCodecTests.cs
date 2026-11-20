using System.Text;
using Microsoft.Extensions.Options;
using OwnDay.Application.Learning;
using OwnDay.Infrastructure.Telegram.Actions;
using OwnDay.Infrastructure.Telegram.Configuration;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Routing;

public sealed class TelegramActionCodecTests
{
    private static readonly DateTimeOffset Expiration = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Theory]
    [InlineData(LearningActionKind.ToggleChoice, 6)]
    [InlineData(LearningActionKind.SubmitChoice, 0)]
    [InlineData(LearningActionKind.Confirm, 0)]
    public void Encode_SignedAction_RoundTripsAcrossInstances(LearningActionKind kind, byte option)
    {
        var action = new LearningAction(kind, Guid.NewGuid(), int.MaxValue, option)
        {
            ChoiceVersion = kind <= LearningActionKind.SubmitChoice ? int.MaxValue : 0
        };
        var wire = Create().Encode(action, 42, 42, Expiration);

        Assert.InRange(Encoding.UTF8.GetByteCount(wire), 1, 64);
        Assert.True(Create().TryDecode(wire, 42, 42, out var decoded, out var expiresAt));
        Assert.Equal(action, decoded);
        Assert.Equal(Expiration, expiresAt);
    }

    [Fact]
    public void TryDecode_InvalidFormatSignatureOrRecipient_RejectsAction()
    {
        var codec = Create();
        var wire = codec.Encode(new(LearningActionKind.Reject, Guid.NewGuid()), 42, 42, Expiration);
        string[] invalid = [wire[..^1], wire + "AAA", "a2." + wire[3..], "a1.💬" + wire[5..],
            wire[..^1] + (wire[^1] == 'A' ? 'B' : 'A')];

        Assert.All(invalid, value => Assert.False(codec.TryDecode(value, 42, 42, out _, out _)));
        Assert.False(codec.TryDecode(wire, 43, 43, out _, out _));
        Assert.False(codec.TryDecode(wire, 42, 43, out _, out _));
        Assert.False(Create("99:token").TryDecode(wire, 42, 42, out _, out _));
    }

    private static TelegramActionCodec Create(string botToken = "123:token") => new(Options.Create(new TelegramOptions
    {
        BotToken = botToken,
        CallbackSigningKey = Convert.ToBase64String(new byte[32])
    }));
}
