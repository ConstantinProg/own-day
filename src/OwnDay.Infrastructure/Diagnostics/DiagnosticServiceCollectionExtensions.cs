using Microsoft.Extensions.DependencyInjection;

namespace OwnDay.Infrastructure.Diagnostics;

internal static class DiagnosticServiceCollectionExtensions
{
    internal static void AddDiagnostics(this IServiceCollection services)
    {
        services.AddOptions<DiagnosticOptions>().BindConfiguration(DiagnosticOptions.SectionName)
            .Validate(options => options.QueueCapacity is >= 1 and <= 2048 &&
                options.BatchSize >= 1 && options.BatchSize <= Math.Min(128, options.QueueCapacity) &&
                options.MaxExportBytes is >= 1024 and <= 20 * 1024 * 1024 &&
                options.ExportTimeoutSeconds is >= 1 and <= 120 &&
                options.DocumentLifetimeMinutes is >= 1 and <= 1440, "Diagnostic resource limits are out of range.")
            .ValidateOnStart();
        services.AddSingleton<DiagnosticJournal>();
        services.AddSingleton<DiagnosticText>();
        services.AddScoped<DiagnosticExportReader>();
        services.AddHostedService<DiagnosticWriter>();
        services.AddHostedService<DiagnosticExportWorker>();
    }
}
