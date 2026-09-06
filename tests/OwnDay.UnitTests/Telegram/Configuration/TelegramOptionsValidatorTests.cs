using OwnDay.Infrastructure.Telegram.Configuration;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Configuration;

public sealed class TelegramOptionsValidatorTests
{
    private readonly TelegramOptionsValidator _validator = new();

    [Fact]
    public void Validate_WithValidConfiguration_Succeeds()
    {
        var options = new TelegramOptions
        {
            BotToken = "bot-token",
            WebhookSecret = "webhook-secret"
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithInvalidBotToken_Fails(string? botToken)
    {
        var options = new TelegramOptions
        {
            BotToken = botToken,
            WebhookSecret = "webhook-secret"
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failures);
        Assert.Contains("Telegram:BotToken is required.", result.Failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithInvalidWebhookSecret_Fails(string? webhookSecret)
    {
        var options = new TelegramOptions
        {
            BotToken = "bot-token",
            WebhookSecret = webhookSecret
        };

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failures);
        Assert.Contains("Telegram:WebhookSecret is required.", result.Failures);
    }
}