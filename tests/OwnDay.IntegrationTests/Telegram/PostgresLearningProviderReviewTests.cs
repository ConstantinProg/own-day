using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Learning;
using OwnDay.Application.Learning.Requests;
using OwnDay.Domain.Learning;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task SaveStep_LeaseExpiresDuringWrite_RollsBackCheckpointAndRenewal()
    {
        await EnqueueAsync("learn", "English");
        using var host = Host(new Probe("generate"), worker: false);
        using var scope = host.Services.CreateScope();
        var lease = (await scope.ServiceProvider.GetRequiredService<ILearningRequestStore>().ClaimAsync(TimeSpan.FromMinutes(1), default))!;
        await using var db = new OwnDayDbContext(new DbContextOptionsBuilder<OwnDayDbContext>()
            .UseNpgsql(_connection).AddInterceptors(new SlowCheckpoint()).Options);
        var step = new LearningProviderStep { Kind = "generate", Input = "{}", Fingerprint = new('A', 64), TargetStamp = new('B', 64) };
        Assert.False(await new EfLearningRequestStore(db).SaveStepAsync(lease, step, TimeSpan.FromMilliseconds(25), default));
        await using var observer = Open();
        Assert.Empty(await observer.Set<LearningProviderStep>().ToListAsync());
        Assert.Equal(lease.Token, (await observer.Set<LearningRequest>().SingleAsync()).LeaseToken);
    }

    [Fact]
    public async Task FindActive_LargeExplanationHistory_ReadsBoundedTailAndPreservesStoredHistory()
    {
        await SeedAsync("learn_ask");
        await using (var setup = Open())
        {
            var lesson = await setup.Set<Lesson>().SingleAsync();
            for (var i = 0; i < 25; i++) lesson.AddExplanation(LessonInteractionKind.Clarification, "Why " + i, "Explanation " + i);
            await setup.SaveChangesAsync();
        }
        await using var db = Open();
        var active = await new EfLessonStore(db).FindActiveAsync(_user, default);
        Assert.Equal(10, active!.Interactions.Count);
        active.AddExplanation(LessonInteractionKind.Clarification, "One more", "More context");
        await db.SaveChangesAsync();
        await using var observer = Open();
        Assert.Equal(26, await observer.Set<LessonInteraction>().CountAsync());
    }

    private sealed class SlowCheckpoint : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(150, cancellationToken);
            return result;
        }
    }
}
