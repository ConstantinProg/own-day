using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

// Exercise recovery of persisted v1 commands through the real worker. Public
// webhook admission and dialogue confirmations are tested separately.
internal static class LearningWorkerFixture
{
    public static async Task ProcessAsync(IServiceProvider services, int sourceId, long owner, string command)
    {
        using (var seed = services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<OwnDayDbContext>();
            var sourceKey = $"telegram:update:{sourceId}";
            if (await db.Set<LearningRequest>().AnyAsync(request => request.SourceKey == sourceKey)) return;
            var parts = command.TrimStart('/').Split(' ', 2);
            var sequence = await db.Set<LearningRequest>().Where(request => request.UserId == new UserId(owner))
                .MaxAsync(request => (long?)request.Sequence) ?? 0;
            db.Add(new LearningRequest
            {
                UserId = new UserId(owner), Sequence = sequence + 1, SourceKey = sourceKey,
                SourceUpdateId = sourceId, SourceMessageId = sourceId, ChatId = owner,
                PayloadVersion = 1, Status = LearningRequestStatus.Ready, Outcome = null,
                ReceivedAt = DateTime.UtcNow, AvailableAt = DateTime.UtcNow,
                NormalizedInput = JsonSerializer.Serialize(new LearningCommandInput(parts[0], parts.Length > 1 ? parts[1] : ""), JsonSerializerOptions.Web)
            });
            await db.SaveChangesAsync();
        }
        using var scope = services.CreateScope();
        var lease = await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>()
            .ClaimAsync(TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        Assert.True(await scope.ServiceProvider.GetRequiredService<LearningRequestProcessor>().ProcessAsync(lease, 3, default));
        var saved = await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().Set<LearningRequest>()
            .AsNoTracking().SingleAsync(request => request.Id == lease.RequestId);
        Assert.Equal(LearningRequestStatus.Completed, saved.Status);
        // The production worker timestamps delivery using the database clock.
        // These historical delivery journeys use a virtual clock; arrange only
        // reply availability so their sender can observe the committed intents.
        var due = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        await scope.ServiceProvider.GetRequiredService<OwnDayDbContext>().TelegramOutboxMessages
            .Where(message => message.LearningRequestId == lease.RequestId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.NextAttemptAt, due));
    }
}
