using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OwnDay.Infrastructure.Telegram.Configuration;
using OwnDay.Infrastructure.Telegram.Handling;
using Telegram.Bot.Types;

namespace OwnDay.Host.Controllers;

[ApiController]
[Route("telegram/webhook")]
public sealed class TelegramWebhookController : ControllerBase
{
    private const string SecretHeaderName =
        "X-Telegram-Bot-Api-Secret-Token";

    private readonly ITelegramUpdateHandler _updateHandler;
    private readonly TelegramOptions _options;

    public TelegramWebhookController(
        ITelegramUpdateHandler updateHandler,
        IOptions<TelegramOptions> options)
    {
        ArgumentNullException.ThrowIfNull(updateHandler);
        ArgumentNullException.ThrowIfNull(options);

        _updateHandler = updateHandler;
        _options = options.Value;
    }

    [HttpPost]
    public async Task<IActionResult> Post(
        [FromBody] Update update,
        CancellationToken cancellationToken)
    {
        if (!HasValidWebhookSecret())
        {
            return Unauthorized();
        }

        await _updateHandler.HandleAsync(update, cancellationToken);

        return Ok();
    }

    private bool HasValidWebhookSecret()
    {
        if (!Request.Headers.TryGetValue(
                SecretHeaderName,
                out var headerValues) ||
            headerValues.Count is not 1)
        {
            return false;
        }

        var providedSecret = headerValues[0];

        if (providedSecret is null ||
            string.IsNullOrEmpty(_options.WebhookSecret))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedSecret);
        var expectedBytes = Encoding.UTF8.GetBytes(_options.WebhookSecret);

        return CryptographicOperations.FixedTimeEquals(
            providedBytes,
            expectedBytes);
    }
}
