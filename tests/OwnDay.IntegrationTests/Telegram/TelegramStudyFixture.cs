using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OwnDay.Application.Learning;
using OwnDay.Domain;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using OwnDay.Infrastructure.Telegram.Delivery;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

internal static class TelegramStudyFixture
{
    internal static void Configure(IServiceCollection services, Clock clock, TelegramCurriculumFixture.Teacher teacher)
    {
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        services.Replace(ServiceDescriptor.Singleton<ICurriculumPlanner>(new TelegramCurriculumFixture.Planner { NoQuestion = true }));
        services.Replace(ServiceDescriptor.Singleton<ILessonTeacher>(teacher));
    }

    internal static async Task<StudySchedule> Dispatch(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StudyInvitationDispatcher>().DispatchBatchAsync(CancellationToken.None);
        return await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<StudySchedule>().AsNoTracking().SingleAsync();
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
