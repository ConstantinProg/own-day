using Microsoft.Extensions.Options;
using OwnDay.Application.Interactions;
using OwnDay.Host.Filters;
using OwnDay.Infrastructure.Telegram;
using OwnDay.Infrastructure.Telegram.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddScoped<TelegramWebhookAuthorizationFilter>();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IIncomingCommandHandler, IncomingCommandHandler>();

builder.Services.AddSingleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>();
builder.Services
    .AddOptions<TelegramOptions>()
    .BindConfiguration(TelegramOptions.SectionName)
    .ValidateOnStart();

builder.Services.AddTelegram();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/version", () => TypedResults.Ok(new
{
    service = "OwnDay.Host",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"
}));

app.Run();

public partial class Program;
