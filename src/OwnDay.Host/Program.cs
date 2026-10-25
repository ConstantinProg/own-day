using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OwnDay.Application.Actions;
using OwnDay.Application.Inbox;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Host.Filters;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram;
using OwnDay.Infrastructure.Telegram.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddScoped<TelegramWebhookAuthorizationFilter>();
builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<OwnDayDbContext>(
        name: "postgresql",
        tags: ["ready"]);
builder.Services.AddScoped<ActionService>();
builder.Services.AddScoped<IActionStore, EfActionStore>();
builder.Services.AddScoped<StructuredItemService>();
builder.Services.AddScoped<IStructuredItemStore, EfStructuredItemStore>();
builder.Services.AddScoped<InboxCaptureService>();
builder.Services.AddScoped<IInboxItemStore, EfInboxItemStore>();
builder.Services.AddScoped<InboxProcessingService>();
builder.Services.AddScoped<InboxDraftService>();
builder.Services.AddScoped<IInboxDraftStore, EfInboxDraftStore>();
builder.Services.AddScoped<IInboxProcessingStore, EfInboxProcessingStore>();
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
builder.Services.AddInboxAnalysis();
builder.Services.AddInboxPolicy();

await using var app = builder.Build();

// An explicit operator command runs the application use case without starting HTTP or hosted workers.
if (builder.Configuration["analyze-inbox-user"] is { } analysisUser)
{
    var userId = long.Parse(analysisUser, CultureInfo.InvariantCulture);
    var itemId = long.Parse(builder.Configuration["analyze-inbox-item"] ??
        throw new InvalidOperationException("--analyze-inbox-item is required."), CultureInfo.InvariantCulture);
    await using var scope = app.Services.CreateAsyncScope();
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        var result = await scope.ServiceProvider.GetRequiredService<InboxAnalysisService>()
            .AnalyzeAsync(new UserId(userId), itemId, cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result?.Id, result?.Version, result?.Status,
            proposals = result?.Proposals.OrderBy(proposal => proposal.Position).Select(proposal => proposal.ReadProposal())
        }, InboxAnalysisJson.Options));
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }

    return;
}

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
    Predicate = healthCheck => healthCheck.Tags.Contains("ready")
});
app.MapGet("/version", () => TypedResults.Ok(new
{
    service = "OwnDay.Host",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"
}));

app.Run();

public partial class Program;
