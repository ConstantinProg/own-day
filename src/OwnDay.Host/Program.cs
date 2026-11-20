using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.StructuredItems;
using OwnDay.Host.Filters;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram;
using OwnDay.Infrastructure.Telegram.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

if (builder.Configuration["analyze-inbox-user"] is not null || builder.Configuration["analyze-inbox-item"] is not null)
{
    throw new InvalidOperationException("The Inbox analysis operator command has been retired.");
}

builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddScoped<TelegramWebhookAuthorizationFilter>();
builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<OwnDayDbContext>(
        name: "postgresql",
        tags: ["ready"])
    .AddCheck<OwnDay.Host.HealthChecks.LearningQueueHealthCheck>("learning_queue", tags: ["ready"]);
builder.Services.AddScoped<ActionService>();
builder.Services.AddScoped<IActionStore, EfActionStore>();
builder.Services.AddScoped<StructuredItemService>();
builder.Services.AddScoped<IStructuredItemStore, EfStructuredItemStore>();
builder.Services.AddScoped<IInboxDraftStore, EfInboxDraftStore>();
builder.Services.AddDbContext<OwnDayDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Default") ??
        throw new InvalidOperationException("ConnectionStrings:Default is required.")));

builder.Services.AddSingleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>();
builder.Services
    .AddOptions<TelegramOptions>()
    .BindConfiguration(TelegramOptions.SectionName)
    .ValidateOnStart();

builder.Services.AddTelegram();
builder.Services.AddOpenAIProvider();

await using var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapControllers();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = healthCheck => healthCheck.Tags.Contains("ready")
});
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = healthCheck => healthCheck.Tags.Contains("ready"),
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(entry => entry.Key, entry => new
        {
            status = entry.Value.Status.ToString(), data = entry.Value.Data
        })
    }, context.RequestAborted)
});
app.MapGet("/version", () => TypedResults.Ok(new
{
    service = "OwnDay.Host",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"
}));

app.Run();

public partial class Program;
