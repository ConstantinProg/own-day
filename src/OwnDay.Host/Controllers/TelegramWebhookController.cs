using Microsoft.AspNetCore.Mvc;
using OwnDay.Host.Filters;
using OwnDay.Infrastructure.Telegram.Handling;
using Telegram.Bot.Types;

namespace OwnDay.Host.Controllers;

[ApiController]
[Route("telegram/webhook")]
[ServiceFilter(typeof(TelegramWebhookAuthorizationFilter))]
public sealed class TelegramWebhookController : ControllerBase
{
    private readonly ITelegramUpdateHandler _updateHandler;

    public TelegramWebhookController(
        ITelegramUpdateHandler updateHandler)
    {
        ArgumentNullException.ThrowIfNull(updateHandler);

        _updateHandler = updateHandler;
    }

    [HttpPost]
    public async Task<IActionResult> Post(
        [FromBody] Update update,
        CancellationToken cancellationToken)
    {
        await _updateHandler.HandleAsync(update, cancellationToken);

        return Ok();
    }
}
