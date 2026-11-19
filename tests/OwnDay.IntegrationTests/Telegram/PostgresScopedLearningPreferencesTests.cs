using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using OwnDay.Domain.Learning;
using Xunit;

namespace OwnDay.IntegrationTests.Telegram;

public sealed partial class PostgresLearningProviderTests
{
    [Fact]
    public async Task Post_SubjectPreferenceAndOneOffRequest_AreIsolatedAcrossProgramsAndSurviveRestart()
    {
        var typescript = LearningProgram.Create(_user, "Read typed APIs", "Beginner", null, "TypeScript", [new("unions", "Unions", [])], null);
        var finance = LearningProgram.Create(_user, "Understand markets", "Beginner", null, "Finance", [new("orders", "Orders", [])], null);
        await using (var db = Open())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261118190000_AddCurriculumPath");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO learning_preferences ("UserId", "Kind", "Value", "UpdatedAt", "Source")
                VALUES ({_user.Value}, {(int)LearningPreferenceKind.Depth}, 'legacy depth', {_now}, 'Explicit user preference')
                """);
            await migrator.MigrateAsync();
            db.AddRange(typescript, finance, LearningContext.Create(typescript));
            await db.SaveChangesAsync();
        }

        var probe = new Probe("");
        using (var host = SwitchHost(probe))
        using (var client = host.CreateClient())
        {
            await SendEnglishAsync(client, 1, "запомни для этой дисциплины depth | detailed types");
            await SendEnglishAsync(client, 2, "/learn_next");
            await SendEnglishAsync(client, 3, "объясни короче");
            await SendEnglishAsync(client, 4, "I have 3 minutes");
            await using var db = Open();
            var lesson = await db.Set<Lesson>().Include(value => value.Interactions).SingleAsync();
            Assert.Equal(probe.Material.Question, lesson.Question);
            Assert.Equal("detailed types", lesson.Teaching!.Preferences.Depth);
            var explanations = lesson.Interactions.OrderBy(value => value.Id).ToArray();
            Assert.Equal("brief", explanations[0].ExplanationPreferences!.Depth);
            Assert.Equal("fit the explanation in 3 minutes", explanations[1].ExplanationPreferences!.Pace);
            Assert.Equal("detailed types", explanations[1].ExplanationPreferences!.Depth);
            Assert.Equal(2, await db.Set<LearningPreference>().CountAsync());
            Assert.Empty(await db.Set<LearningEvidence>().ToListAsync());
        }

        using var restarted = SwitchHost(probe);
        using var resumed = restarted.CreateClient();
        await SendEnglishAsync(resumed, 5, "/learn_preferences");
        await SendEnglishAsync(resumed, 6, "switch to Finance");
        await SendEnglishAsync(resumed, 7, "/learn_confirm " + await SwitchCodeAsync());
        await SendEnglishAsync(resumed, 8, "/learn_next");
        await SendEnglishAsync(resumed, 9, "выбрать программу TypeScript");
        await SendEnglishAsync(resumed, 10, "/learn_confirm " + await SwitchCodeAsync());
        await SendEnglishAsync(resumed, 11, "/learn_preference_reset subject all");

        await using var result = Open();
        var different = await result.Set<Lesson>().SingleAsync(lesson => lesson.ProgramId == finance.Id);
        Assert.Equal("legacy depth", different.Teaching!.Preferences.Depth);
        var settings = await result.Set<LearningPreference>().ToArrayAsync();
        Assert.Equal("legacy depth", Assert.Single(settings, setting => setting.IsExplicit()).Value);
        Assert.Equal("TYPESCRIPT", Assert.Single(settings, setting => !setting.IsExplicit()).Scope);
        Assert.False(result.Database.HasPendingModelChanges());
        var rollback = await Assert.ThrowsAsync<PostgresException>(() =>
            result.GetService<IMigrator>().MigrateAsync("20261118190000_AddCurriculumPath"));
        Assert.Contains("Retain scoped learning preferences", rollback.MessageText, StringComparison.Ordinal);
    }
}
