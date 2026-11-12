using System.Text.Json;
using OwnDay.Application.Dialogue;
using Xunit;

namespace OwnDay.UnitTests.Dialogue;

public sealed class LearningIntentProposalTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Intent\":1}")]
    [InlineData("{\"Intent\":\"1\"}")]
    [InlineData("{\"Intent\":\"DeleteAllData\"}")]
    [InlineData("{\"Intent\":\"Answer\",\"TargetId\":\"foreign\"}")]
    [InlineData("{\"Intent\":\"Answer\",\"Risk\":\"safe\"}")]
    [InlineData("{\"Intent\":\"Answer\",\"Intent\":\"Pause\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Parse_InvalidOrOverreachingProposal_Rejects(string json) =>
        Assert.Throws<JsonException>(() => LearningIntentProposal.Parse(json));

    [Fact]
    public void Parse_AllowedProposal_ReturnsIntentOnly() =>
        Assert.Equal(LearningIntent.Answer, LearningIntentProposal.Parse("{\"Intent\":\"Answer\"}").Intent);
}
