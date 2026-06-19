using Microsoft.AspNetCore.Mvc;
using Telegram.Bot.Types;

namespace OwnDay.App.Controllers;

[ApiController]
[Route("telegram/webhook")]
public sealed class TelegramWebhookController : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] Update update)
    {
        return Ok();
    }
}
