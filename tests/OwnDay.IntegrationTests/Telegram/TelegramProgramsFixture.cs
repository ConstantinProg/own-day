using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

internal static class TelegramProgramsFixture
{
    internal static void Configure(IServiceCollection services, TelegramCurriculumFixture.Planner planner, TelegramCurriculumFixture.Teacher teacher)
    {
        services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(planner));
        services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
    }

}
