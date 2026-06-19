using OwnDay.Application;
using OwnDay.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console();
});

builder.Services.AddControllers();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services
    .AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Default")!);

var app = builder.Build();

app.UseSerilogRequestLogging();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/version", () => Results.Ok(new
{
    service = "OwnDay.App",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"
}));

app.Run();
