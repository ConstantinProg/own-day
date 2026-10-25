using System.Text.Json;
using System.Text.Json.Nodes;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain.Inbox;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Analysis;

public sealed class InboxAnalysisValidatorTests
{
    private static readonly InboxAnalysisRequest Request = new(
        "купит молоко; сделать ремонт; жду от Анны с 2026-09-26T09:00:00Z; ссылка; может завтра в горы",
        new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc),
        new(null, [new(12, "Ремонт", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc))], false),
        InboxAnalysisService.SupportedTypes);

    [Theory]
    [InlineData(InboxTargetKind.Action, "Купить молоко")]
    [InlineData(InboxTargetKind.Project, "Сделать ремонт")]
    [InlineData(InboxTargetKind.WaitingFor, "Ответ Анны")]
    [InlineData(InboxTargetKind.Reference, "Ссылка")]
    [InlineData(InboxTargetKind.SomedayMaybe, "Возможно, сходить в горы")]
    public void ParseAndValidate_SupportedKind_PreservesCanonicalProposal(InboxTargetKind kind, string title)
    {
        var proposal = Proposal(kind, title);
        var parsed = new InboxAnalysisValidator().ParseAndValidate(Json(proposal), Request);

        Assert.NotNull(parsed);
        Assert.Equal(title, Assert.Single(parsed.Items).Title);
        Assert.Equal(kind, parsed.Items[0].Kind);
    }

    [Fact]
    public void ParseAndValidate_MultipleItems_ExtractsSupportedPropertiesAndPreservesUnresolvedDate()
    {
        var waiting = Proposal(InboxTargetKind.WaitingFor, "Ответ Анны") with
        {
            Properties = new()
            {
                Source = "Анны", WaitingSince = DateTimeOffset.Parse("2026-09-26T09:00:00Z"),
                WaitingSinceEvidence = "2026-09-26T09:00:00Z", UnresolvedDateText = null
            },
            ProjectIds = [12]
        };
        var idea = Proposal(InboxTargetKind.SomedayMaybe, "Возможно, сходить в горы") with
        {
            Properties = EmptyProperties() with { UnresolvedDateText = "завтра" },
            Ambiguities = ["Часовой пояс неизвестен; дата не определена."]
        };
        var parsed = new InboxAnalysisValidator().ParseAndValidate(Json(waiting, idea), Request);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Items.Length);
        Assert.Equal("Анны", parsed.Items[0].Properties.Source);
        Assert.Equal(TimeSpan.Zero, parsed.Items[0].Properties.WaitingSince!.Value.Offset);
        Assert.Equal(12, Assert.Single(parsed.Items[0].ProjectIds));
        Assert.Null(parsed.Items[1].Properties.WaitingSince);
        Assert.Equal("завтра", parsed.Items[1].Properties.UnresolvedDateText);
    }

    public static TheoryData<string> MalformedResponses => new()
    {
        "not json", "null", "[]", "{}", "{\"items\":null}", "{\"items\":[]}", "{\"items\":[null]}",
        "{\"items\":[],\"items\":[]}", "{\"items\":[],\"instruction\":\"apply\"}"
    };

    [Theory]
    [MemberData(nameof(MalformedResponses))]
    public void ParseAndValidate_MalformedStructure_Rejects(string json) =>
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(json, Request));

    [Theory]
    [InlineData("kind", "Unknown")]
    [InlineData("kind", "1")]
    [InlineData("title", "")]
    [InlineData("title", "   ")]
    [InlineData("title", " padded ")]
    [InlineData("description", "")]
    public void ParseAndValidate_InvalidScalar_Rejects(string property, string value)
    {
        var node = JsonNode.Parse(Json(Proposal(InboxTargetKind.Action, "Купить молоко")))!;
        node["items"]![0]![property] = value;
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(node.ToJsonString(), Request));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("properties")]
    [InlineData("projectIds")]
    [InlineData("ambiguities")]
    public void ParseAndValidate_MissingRequiredField_Rejects(string property)
    {
        var node = JsonNode.Parse(Json(Proposal(InboxTargetKind.Action, "Купить молоко")))!;
        node["items"]![0]!.AsObject().Remove(property);
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(node.ToJsonString(), Request));
    }

    [Fact]
    public void ParseAndValidate_NumericEnumAndUnsupportedProperty_Rejects()
    {
        var node = JsonNode.Parse(Json(Proposal(InboxTargetKind.Action, "Купить молоко")))!;
        node["items"]![0]!["kind"] = 0;
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(node.ToJsonString(), Request));
        node["items"]![0]!["kind"] = "Action";
        node["items"]![0]!["properties"]!["deadline"] = "2027-01-01";
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(node.ToJsonString(), Request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    public void ParseAndValidate_ProjectOutsideSnapshot_Rejects(long id) =>
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(
            Json(Proposal(InboxTargetKind.Action, "Купить молоко") with { ProjectIds = [id] }), Request));

    [Fact]
    public void ParseAndValidate_StructuralContradictions_Rejects()
    {
        InboxProposal[] invalid =
        [
            Proposal(InboxTargetKind.Project, "Ремонт") with { ProjectIds = [12] },
            Proposal(InboxTargetKind.Action, "Купить молоко") with { ProjectIds = [12, 12] },
            Proposal(InboxTargetKind.Action, "Купить молоко") with { Properties = EmptyProperties() with { Source = "Анны" } },
            Proposal(InboxTargetKind.WaitingFor, "Ответ") with { Properties = EmptyProperties() with { Source = "Unknown person" } },
            Proposal(InboxTargetKind.Action, new string('x', 201)),
            Proposal(InboxTargetKind.WaitingFor, "Ответ") with
            {
                Properties = EmptyProperties() with
                {
                    WaitingSince = Request.CapturedAt.AddDays(1), WaitingSinceEvidence = "завтра"
                }
            },
            Proposal(InboxTargetKind.Action, "Купить молоко") with
            {
                Properties = EmptyProperties() with { UnresolvedDateText = "завтра" }
            }
        ];
        foreach (var proposal in invalid)
        {
            Assert.Null(new InboxAnalysisValidator().ParseAndValidate(Json(proposal), Request));
        }
    }

    [Fact]
    public void ParseAndValidate_ExcessiveOutput_Rejects()
    {
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(new string('x', 64_001), Request));
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(
            Json(Enumerable.Repeat(Proposal(InboxTargetKind.Action, "Купить молоко"), 21).ToArray()), Request));
    }

    [Theory]
    [InlineData("2026-09-26T09:00:00", "2026-09-26T09:00:00Z")]
    [InlineData("2026-09-26T09:00:00Z", "yesterday")]
    [InlineData("2026-09-26T09:00:00Z", "2026-09-26T09:00:00")]
    [InlineData("2026-09-26T09:00:00Z", null)]
    [InlineData("2026-09-26T10:00:00Z", "2026-09-26T09:00:00Z")]
    [InlineData("2026-09-26T12:00:00+03:00", "2026-09-26T09:00:00Z")]
    public void ParseAndValidate_UnverifiableOrNonUtcInstant_RejectsWithoutAssumingTimezone(string instant, string? evidence)
    {
        var node = JsonNode.Parse(Json(Proposal(InboxTargetKind.WaitingFor, "Ответ Анны")))!;
        node["items"]![0]!["properties"]!["waitingSince"] = instant;
        node["items"]![0]!["properties"]!["waitingSinceEvidence"] = evidence;
        Assert.Null(new InboxAnalysisValidator().ParseAndValidate(node.ToJsonString(), Request));
    }

    internal static InboxProposal Proposal(InboxTargetKind kind, string title) => new()
    {
        Kind = kind, Title = title, Description = null, Properties = EmptyProperties(), ProjectIds = [], Ambiguities = []
    };

    private static InboxProposalProperties EmptyProperties() => new()
    {
        Source = null, WaitingSince = null, WaitingSinceEvidence = null, UnresolvedDateText = null
    };

    internal static string Json(params InboxProposal[] proposals) =>
        JsonSerializer.Serialize(new InboxAnalysisOutput { Items = proposals }, InboxAnalysisJson.Options);
}
