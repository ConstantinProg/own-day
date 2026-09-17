using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using OwnDay.Infrastructure.Telegram.Configuration;

namespace OwnDay.Host.Filters;

public sealed class TelegramWebhookAuthorizationFilter : IAuthorizationFilter
{
    private const string SecretHeaderName = "X-Telegram-Bot-Api-Secret-Token";
    private readonly TelegramOptions _options;

    public TelegramWebhookAuthorizationFilter(IOptions<TelegramOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        // Authorization filters run before MVC reads and binds the request body.
        if (!HasValidWebhookSecret(context.HttpContext.Request))
        {
            context.Result = new UnauthorizedResult();
        }
    }

    private bool HasValidWebhookSecret(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(SecretHeaderName, out var headerValues) ||
            headerValues.Count is not 1)
        {
            return false;
        }

        var providedSecret = headerValues[0];

        if (providedSecret is null || string.IsNullOrEmpty(_options.WebhookSecret))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedSecret),
            Encoding.UTF8.GetBytes(_options.WebhookSecret));
    }
}
