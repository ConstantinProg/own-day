using OwnDay.Infrastructure.Telegram.Handling;
using Xunit;

namespace OwnDay.UnitTests.Telegram.Handling;

public sealed class TelegramTextListsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Tasks")]
    public void Split_ManyLines_PreservesOrderAndMessageLimit(string heading)
    {
        var lines = Enumerable.Range(1, 50).Select(index => $"#{index} {new string('x', 190)}").ToArray();

        var messages = TelegramTextLists.Split(lines, heading);

        Assert.True(messages.Count > 1);
        Assert.All(messages, message => Assert.InRange(message.Length, 1, 4096));
        var renderedLines = messages.SelectMany(message => message.Split('\n'));
        Assert.Equal(lines, heading.Length == 0 ? renderedLines : renderedLines.Where(line => line != heading));
        if (heading.Length > 0)
        {
            Assert.All(messages, message => Assert.StartsWith(heading + "\n", message, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Split_OversizedLine_AccountsForHeadingAndTruncatesWithinLimit()
    {
        var heading = new string('h', 100);

        var message = Assert.Single(TelegramTextLists.Split([new string('x', 5000)], heading));

        Assert.Equal(4096, message.Length);
        Assert.StartsWith(heading + "\n", message, StringComparison.Ordinal);
        Assert.EndsWith("…", message, StringComparison.Ordinal);
    }
}
