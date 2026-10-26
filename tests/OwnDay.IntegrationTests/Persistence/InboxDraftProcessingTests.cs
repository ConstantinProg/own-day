using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OwnDay.Application.Inbox;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.Infrastructure.Persistence;
using Xunit;

namespace OwnDay.IntegrationTests.Persistence;

public sealed class InboxDraftProcessingTests
{
    private static readonly UserId Owner = new(101);

    [Theory]
    [InlineData(InboxTargetKind.Action)]
    [InlineData(InboxTargetKind.Project)]
    [InlineData(InboxTargetKind.SomedayMaybe)]
    [InlineData(InboxTargetKind.Reference)]
    [InlineData(InboxTargetKind.WaitingFor)]
    public async Task ProcessDirectAsync_WithoutReplacementText_CreatesUnlinkedRecordFromOriginal(InboxTargetKind kind)
    {
        using var factory = new OwnDayHostFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var capture = scope.ServiceProvider.GetRequiredService<InboxCaptureService>();
        var drafts = scope.ServiceProvider.GetRequiredService<InboxDraftService>();
        var item = await capture.CaptureAsync(Owner, "Original", CancellationToken.None);

        var result = await drafts.ProcessDirectAsync(Owner, item.Id, kind, null, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var processed = Assert.IsType<InboxDraftResult.Processed>(result);
        Assert.Equal(kind, processed.TargetKind);
        var persisted = await db.InboxItems.SingleAsync();
        Assert.Equal(InboxItemStatus.Processed, persisted.Status);
        Assert.Equal("Original", persisted.OriginalText);
        Assert.Equal(processed.TargetId, Assert.Single(persisted.Targets).TargetId);
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        var text = kind switch
        {
            InboxTargetKind.Action => (await db.Actions.SingleAsync()).Title,
            InboxTargetKind.Project => (await db.Projects.SingleAsync()).Title,
            InboxTargetKind.SomedayMaybe => (await db.SomedayMaybes.SingleAsync()).Text,
            InboxTargetKind.Reference => (await db.References.SingleAsync()).Text,
            InboxTargetKind.WaitingFor => (await db.WaitingFors.SingleAsync()).Description,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.Equal("Original", text);
        Assert.False(await db.Actions.AnyAsync(record => record.ProjectId != null));
        Assert.False(await db.SomedayMaybes.AnyAsync(record => record.ProjectId != null));
        Assert.False(await db.References.AnyAsync(record => record.ProjectId != null));
        Assert.False(await db.WaitingFors.AnyAsync(record => record.ProjectId != null || record.Source != null));
    }

    [Fact]
    public async Task ProcessDirectAsync_TextTooLong_PreservesOriginalAndAllowsCorrectionInEditor()
    {
        using var factory = new OwnDayHostFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var capture = scope.ServiceProvider.GetRequiredService<InboxCaptureService>();
        var drafts = scope.ServiceProvider.GetRequiredService<InboxDraftService>();
        var item = await capture.CaptureAsync(Owner, new string('x', 201), CancellationToken.None);

        var result = await drafts.ProcessDirectAsync(Owner, item.Id, InboxTargetKind.Project, null, CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(InboxDraftOutcome.InvalidText, result.Outcome);
        Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.SingleAsync()).Status);
        Assert.Empty(await db.Projects.ToListAsync());
        var prompt = Assert.IsType<InboxDraftResult.Prompt>(await drafts.CaptureOrLoadAsync(Owner, "Corrected", CancellationToken.None));
        Assert.Equal(InboxDraftStep.Text, prompt.Draft.Step);
        await drafts.AdvanceAsync(Owner, prompt.Draft, prompt.Item, new InboxDraftInput.EditText("Corrected"), CancellationToken.None);
        var corrected = await drafts.AdvanceAsync(Owner, prompt.Draft, prompt.Item, new InboxDraftInput.Confirm(), CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.IsType<InboxDraftResult.Processed>(corrected);
        Assert.Equal("Corrected", (await db.Projects.SingleAsync()).Title);
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessDirectAsync_ForeignOrMissingItem_DoesNotCreateDraftOrTarget(bool missing)
    {
        using var factory = new OwnDayHostFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OwnDayDbContext>();
        var capture = scope.ServiceProvider.GetRequiredService<InboxCaptureService>();
        var drafts = scope.ServiceProvider.GetRequiredService<InboxDraftService>();
        var item = await capture.CaptureAsync(Owner, "Original", CancellationToken.None);

        var result = await drafts.ProcessDirectAsync(new UserId(102), missing ? item.Id + 1 : item.Id,
            InboxTargetKind.Project, "Replacement", CancellationToken.None);

        Assert.Equal(InboxDraftOutcome.NotFound, result.Outcome);
        Assert.Empty(await db.TelegramInboxDrafts.ToListAsync());
        Assert.Empty(await db.Projects.ToListAsync());
        Assert.Equal(InboxItemStatus.Active, (await db.InboxItems.SingleAsync()).Status);
    }
}
