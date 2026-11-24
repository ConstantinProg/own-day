using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OwnDay.Application.TesterMode;
using OwnDay.Infrastructure.Diagnostics;
using OwnDay.Infrastructure.OpenAIAnalysis;
using OwnDay.Infrastructure.Telegram.Configuration;
using Xunit;

namespace OwnDay.UnitTests.Diagnostics;

public sealed class DiagnosticJournalTests
{
    [Theory]
    [InlineData("", DiagnosticExportMode.Full)]
    [InlineData(" recent ", DiagnosticExportMode.Recent)]
    [InlineData("LAST", DiagnosticExportMode.Last)]
    public void TryParse_SupportedCommand_ReturnsMode(string argument, DiagnosticExportMode expected)
    {
        Assert.True(DiagnosticExportCommand.TryParse(argument, out var mode));
        Assert.Equal(expected, mode);
        Assert.False(DiagnosticExportCommand.TryParse("last 202", out _));
    }

    [Fact]
    public void Write_OrdinaryMode_DoesNotEvaluateDetailAndCountsOverflow()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var journal = new DiagnosticJournal(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DiagnosticOptions { QueueCapacity = 1 }), new TesterModeOptions(), TimeProvider.System);
        using var current = DiagnosticJournal.Enter(new(42, Guid.Empty, false, Guid.NewGuid(), null, null));
        var evaluated = false;
        journal.Write("llm", "llm_call_started", new(Model: "test-model"), detail: () =>
        {
            evaluated = true;
            return new { prompt = "tester only" };
        });
        journal.Write("learning", "request_completed");
        Assert.False(evaluated);
        Assert.Equal(1, journal.Dropped);
        Assert.True(journal.Reader.TryRead(out var row));
        Assert.Equal("minimal", row.Detail);
        Assert.DoesNotContain("tester only", row.Data);
    }

    [Fact]
    public void Write_TesterMode_PreservesAllowedTextAndExcludesCredentialsAndCallbackTokens()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var journal = new DiagnosticJournal(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DiagnosticOptions()), new TesterModeOptions(), TimeProvider.System);
        var text = new DiagnosticText(Options.Create(new TelegramOptions { WebhookSecret = "test-webhook-value" }),
            Options.Create(new OpenAIInboxAnalysisOptions()));
        using var current = DiagnosticJournal.Enter(new(42, Guid.Empty, true, Guid.NewGuid(), null, null));
        journal.Write("telegram", "message", detail: () => new
        {
            allowed = text.Filter("My exact answer."),
            credential = text.Filter("test-webhook-value"),
            anotherCredential = text.Filter("""{"password":"should-not-appear"}"""),
            code = text.Filter("ownday-tester-123"),
            keyboard = text.Keyboard("""{"inline_keyboard":[[{"text":"Next","callback_data":"private-signed-data"}]]}""")
        });

        Assert.True(journal.Reader.TryRead(out var row));
        Assert.Equal("tester", row.Detail);
        using var payload = JsonDocument.Parse(row.Data);
        var detail = payload.RootElement.GetProperty("detail");
        Assert.Equal("My exact answer.", detail.GetProperty("allowed").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("credential").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("anotherCredential").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("code").ValueKind);
        Assert.Contains("Next", row.Data);
        Assert.DoesNotContain("private-signed-data", row.Data);
    }

    [Fact]
    public void Keyboard_RenderedNavigation_ExportsDisplayLabelAndSafeAction()
    {
        var text = new DiagnosticText(Options.Create(new TelegramOptions()), Options.Create(new OpenAIInboxAnalysisOptions()));
        var markup = OwnDay.Infrastructure.Telegram.Handling.TelegramNavigation.Keyboard("start", "en");
        var button = JsonSerializer.SerializeToElement(text.Keyboard(markup))[0][0];
        Assert.False(string.IsNullOrWhiteSpace(button.GetProperty("text").GetString()));
        Assert.Equal("callback", button.GetProperty("kind").GetString());
        Assert.Equal("list", button.GetProperty("action").GetString());
        Assert.DoesNotContain("nav:", button.GetRawText());
    }

    [Fact]
    public async Task WriteAsync_ExceedsLimit_RejectsWithoutWritingPastLimit()
    {
        using var destination = new MemoryStream();
        using var limited = new DiagnosticLimitedStream(destination, 3);
        await limited.WriteAsync(new byte[] { 1, 2 });
        await Assert.ThrowsAsync<DiagnosticExportLimitException>(async () => await limited.WriteAsync(new byte[] { 3, 4 }));
        Assert.Equal(new byte[] { 1, 2 }, destination.ToArray());
    }
}
