using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Actions;
using OwnDay.Application.Learning;
using OwnDay.Application.StructuredItems;
using OwnDay.Domain;
using OwnDay.Domain.Actions;
using OwnDay.Domain.Learning;
using OwnDay.Domain.Projects;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

internal static class TelegramProjectLearningFixture
{
    internal static async Task<(long ProjectId, long ActionId)> SeedAsync(IServiceProvider services, long user, string title, string task)
    {
        using var scope = services.CreateScope();
        var project = await scope.ServiceProvider.GetRequiredService<StructuredItemService>().CreateProjectAsync(new(user), title, CancellationToken.None);
        var action = await scope.ServiceProvider.GetRequiredService<ActionService>().AddAsync(new(user), task, project.Id, CancellationToken.None);
        return (project.Id, action.Id);
    }

}
