using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Application.Inbox.Policy;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;
using OwnDay.InboxEvaluation;
using Xunit;

namespace OwnDay.UnitTests.Inbox.Policy;

public sealed class InboxPolicyEngineTests
{
    private static readonly UserId Owner = new(41);
    private static readonly DateTime CapturedAt = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);
    private const string Original = "Жду от Ивана с 2026-10-20T09:00:00Z; завтра позвонить";
    private static readonly InboxProposalProperties Empty = new()
    {
        Source = null, WaitingSince = null, WaitingSinceEvidence = null, UnresolvedDateText = null
    };

    public static TheoryData<InboxTargetKind, bool> Kinds
    {
        get
        {
            var data = new TheoryData<InboxTargetKind, bool>();
            foreach (var kind in Enum.GetValues<InboxTargetKind>())
            {
                data.Add(kind, false);
                data.Add(kind, true);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Evaluate_AllKindsWithOrWithoutAmbiguity_NeverAuthorizesAutonomousApplication(InboxTargetKind kind, bool ambiguous)
    {
        var snapshot = Snapshot(Proposal(kind) with { Ambiguities = ambiguous ? ["Review actor"] : [] });
        var result = Evaluate(snapshot);

        Assert.Equal(InboxPolicyRoute.RequireConfirmation, result.Route);
        Assert.Contains(InboxPolicyReason.AutonomousApplicationNotValidated, result.Reasons);
        Assert.Equal(ambiguous, result.Reasons.Contains(InboxPolicyReason.AmbiguityRequiresReview));
        Assert.Equal(snapshot.Analysis.Proposals[0].Id, Assert.Single(result.Proposals).ProposalId);
    }

    public static TheoryData<InboxProposal, InboxPolicyReason> UnsupportedOrInvalidProposals => new()
    {
        { Proposal() with { Description = "Essential extra detail" }, InboxPolicyReason.DescriptionNotSupported },
        { Proposal() with
            {
                Properties = Empty with { UnresolvedDateText = "завтра" }, Ambiguities = ["Date"]
            }, InboxPolicyReason.UnresolvedDateNotSupported },
        { Proposal(InboxTargetKind.WaitingFor) with { Properties = Empty }, InboxPolicyReason.WaitingStartRequired },
        { Proposal() with { ProjectIds = [1, 2], Ambiguities = ["Choose"] }, InboxPolicyReason.ProjectChoiceRequired },
        { Proposal() with { Title = " " }, InboxPolicyReason.InvalidProposal },
        { Proposal() with { Title = new string('a', 201) }, InboxPolicyReason.InvalidProposal },
        { Proposal() with { Properties = Empty with { Source = "Ивана" } }, InboxPolicyReason.InvalidProposal },
        { Proposal(InboxTargetKind.WaitingFor) with
            {
                Properties = Empty with { Source = "Peter" }
            }, InboxPolicyReason.InvalidProposal },
        { Proposal() with { ProjectIds = [99] }, InboxPolicyReason.InvalidProposal },
        { Proposal(InboxTargetKind.Project) with { ProjectIds = [1] }, InboxPolicyReason.InvalidProposal },
        { Proposal(InboxTargetKind.WaitingFor) with
            {
                Properties = Empty with
                {
                    WaitingSince = new DateTimeOffset(CapturedAt.AddDays(1)),
                    WaitingSinceEvidence = "2026-10-26T10:00:00Z"
                }
            }, InboxPolicyReason.InvalidProposal },
        { Proposal(InboxTargetKind.WaitingFor) with
            {
                Properties = Empty with { WaitingSinceEvidence = "2026-10-20T09:00:00Z" }
            }, InboxPolicyReason.InvalidProposal },
        { Proposal() with
            {
                Properties = Empty with { UnresolvedDateText = "завтра" }, Ambiguities = []
            }, InboxPolicyReason.InvalidProposal },
        { Proposal() with { Properties = null! }, InboxPolicyReason.InvalidProposal },
        { Proposal() with { ProjectIds = null! }, InboxPolicyReason.InvalidProposal }
    };

    [Theory]
    [MemberData(nameof(UnsupportedOrInvalidProposals))]
    public void Evaluate_UnsupportedOrInvalidData_KeepsInInbox(InboxProposal proposal, InboxPolicyReason expected)
    {
        var result = Evaluate(Snapshot(proposal));

        Assert.Equal(InboxPolicyRoute.KeepInInbox, result.Route);
        Assert.Contains(expected, result.Reasons);
        Assert.Equal(InboxPolicyRoute.KeepInInbox, Assert.Single(result.Proposals).Route);
    }

    [Fact]
    public void Evaluate_ReferencedProjectUnavailable_KeepsInInbox()
    {
        var snapshot = Snapshot(Proposal() with { ProjectIds = [1] }) with { ActiveOwnedProjects = [] };

        var result = Evaluate(snapshot);

        Assert.Equal(InboxPolicyRoute.KeepInInbox, result.Route);
        Assert.Contains(InboxPolicyReason.ProjectUnavailable, result.Reasons);
        Assert.Equal(InboxPolicyRoute.KeepInInbox, Assert.Single(result.Proposals).Route);
    }

    [Fact]
    public void Evaluate_ReferencedProjectChanged_KeepsInInbox()
    {
        var snapshot = Snapshot(Proposal() with { ProjectIds = [1] }) with
        {
            ActiveOwnedProjects = [new(1, "Renamed", CapturedAt)]
        };

        var result = Evaluate(snapshot);

        Assert.Equal(InboxPolicyRoute.KeepInInbox, result.Route);
        Assert.Contains(InboxPolicyReason.ProjectChanged, result.Reasons);
        Assert.Equal(InboxPolicyRoute.KeepInInbox, Assert.Single(result.Proposals).Route);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_CompositeWithAnyBlockedElement_BlocksWholeBatch(bool blockedElement)
    {
        var snapshot = Snapshot(Proposal(), Proposal(InboxTargetKind.Reference) with
        {
            Description = blockedElement ? "Measurement details" : null
        });
        var result = Evaluate(snapshot);

        Assert.Equal(InboxPolicyRoute.KeepInInbox, result.Route);
        Assert.Contains(InboxPolicyReason.CompositeRequiresJointReview, result.Reasons);
        Assert.Contains(InboxPolicyReason.MultipleTargetsNotSupported, result.Reasons);
        Assert.Equal(InboxPolicyRoute.RequireConfirmation, result.Proposals[0].Route);
        Assert.DoesNotContain(result.Proposals, p => p.Route == InboxPolicyRoute.AutoApply);
    }

    [Theory]
    [InlineData(InboxAnalysisStatus.Pending)]
    [InlineData(InboxAnalysisStatus.Failed)]
    [InlineData(InboxAnalysisStatus.InvalidResponse)]
    public void Evaluate_UnsuccessfulAnalysis_KeepsInInbox(InboxAnalysisStatus status)
    {
        var snapshot = Snapshot(status: status);
        Assert.Contains(InboxPolicyReason.AnalysisNotCompleted, Evaluate(snapshot).Reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_InactiveInbox_BlocksEvenCompletedAnalysis(bool processed)
    {
        var snapshot = Snapshot(Proposal());
        if (processed)
        {
            snapshot.Item.Process(InboxTargetKind.Action, 1, CapturedAt);
        }
        else
        {
            snapshot.Item.Discard(CapturedAt);
        }
        Assert.Contains(InboxPolicyReason.InboxInactive, Evaluate(snapshot).Reasons);
    }

    [Fact]
    public void Evaluate_IdentityContextInputAndRevisionChecks_FailClosed()
    {
        var snapshot = Snapshot(Proposal());
        Assert.Contains(InboxPolicyReason.OwnershipMismatch,
            new InboxPolicyEngine().Evaluate(new UserId(42), 7, snapshot.Analysis.Id, snapshot, InboxPolicyEngine.LegacyVersion).Reasons);
        Assert.Contains(InboxPolicyReason.AnalysisUnavailable,
            new InboxPolicyEngine().Evaluate(Owner, 7, Guid.NewGuid(), null, InboxPolicyEngine.LegacyVersion).Reasons);
        Assert.Contains(InboxPolicyReason.AnalysisSuperseded,
            Evaluate(snapshot with { LatestAnalysisVersion = 2 }).Reasons);
        Assert.Contains(InboxPolicyReason.InputChanged, Evaluate(Snapshot(Proposal(), inputHash: "stale")).Reasons);
        Assert.Contains(InboxPolicyReason.InvalidContext, Evaluate(Snapshot(Proposal(), contextJson: "{}")).Reasons);
        Assert.Contains(InboxPolicyReason.SchemaUnsupported, Evaluate(Snapshot(Proposal(), schema: "inbox-v2")).Reasons);
        Assert.Contains(InboxPolicyReason.InvalidProposalSet, Evaluate(Snapshot()).Reasons);
        snapshot.Analysis.Proposals.Add(snapshot.Analysis.Proposals[0]);
        Assert.Contains(InboxPolicyReason.InvalidProposalSet, Evaluate(snapshot).Reasons);
    }

    [Fact]
    public void Evaluate_RepeatedAndVersionPinnedEvaluation_IsStableAndHasNoSideEffects()
    {
        var snapshot = Snapshot(Proposal() with { ProjectIds = [1] });
        var before = JsonSerializer.Serialize(snapshot, InboxAnalysisJson.Options);
        var first = Evaluate(snapshot);
        var second = Evaluate(snapshot);
        var changedVersion = new InboxPolicyEngine().Evaluate(Owner, 7, snapshot.Analysis.Id, snapshot, "inbox-policy-unknown");

        Assert.Equal(JsonSerializer.Serialize(first, InboxAnalysisJson.Options), JsonSerializer.Serialize(second, InboxAnalysisJson.Options));
        Assert.Equal(before, JsonSerializer.Serialize(snapshot, InboxAnalysisJson.Options));
        Assert.Equal(InboxPolicyEngine.LegacyVersion, first.PolicyVersion);
        Assert.Equal("inbox-policy-unknown", changedVersion.PolicyVersion);
        Assert.Equal(InboxPolicyRoute.KeepInInbox, changedVersion.Route);
        Assert.Contains(InboxPolicyReason.PolicyVersionUnsupported, changedVersion.Reasons);
        Assert.Contains(InboxPolicyReason.ProjectLinkRequiresReview, first.Reasons);
        Assert.DoesNotContain(InboxPolicyReason.ProjectUnavailable, first.Reasons);
        Assert.NotEqual(first.StateFingerprint, Evaluate(snapshot with { ActiveOwnedProjects = [] }).StateFingerprint);
        Assert.DoesNotContain(Original, JsonSerializer.Serialize(first, InboxAnalysisJson.Options));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"timeZoneId\":null,\"projects\":null,\"projectsTruncated\":false}")]
    [InlineData("{\"timeZoneId\":null,\"projects\":[],\"projectsTruncated\":false,\"unknown\":1}")]
    public void Evaluate_InvalidStoredContext_ReturnsTypedBlock(string contextJson)
    {
        var result = Evaluate(Snapshot(Proposal(), contextJson: contextJson));
        Assert.Equal(InboxPolicyRoute.KeepInInbox, result.Route);
        Assert.Contains(InboxPolicyReason.InvalidContext, result.Reasons);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"Action\",\"title\":\"Review\",\"description\":null,\"properties\":{},\"projectIds\":[],\"ambiguities\":[],\"confidence\":1}")]
    public void Evaluate_MalformedStoredPayload_ReturnsTypedBlock(string payload)
    {
        var snapshot = Snapshot(Proposal());
        typeof(InboxAnalysisProposal).GetProperty(nameof(InboxAnalysisProposal.PayloadJson))!.SetValue(snapshot.Analysis.Proposals[0], payload);
        Assert.Contains(InboxPolicyReason.InvalidProposal, Evaluate(snapshot).Reasons);
    }

    [Fact]
    public void Evaluate_TruncatedContextAndRedundantDescription_StillRequiresReview()
    {
        var snapshot = Snapshot(Proposal() with { Description = "Review" },
            context: new InboxAnalysisContext(null, [], true));
        var result = Evaluate(snapshot);
        Assert.Equal(InboxPolicyRoute.RequireConfirmation, result.Route);
        Assert.Contains(InboxPolicyReason.ContextTruncated, result.Reasons);
        Assert.DoesNotContain(InboxPolicyReason.DescriptionNotSupported, result.Reasons);
    }

    [Fact]
    public async Task Evaluate_GoldenDatasetInterpretations_NeverAutoAppliesAndBlocksCompositeAndUnrepresentableData()
    {
        var dataset = await GoldenDataset.LoadAsync(Path.Combine(AppContext.BaseDirectory, "GoldenDataset", "golden-v2.json"), CancellationToken.None);
        foreach (var scenario in dataset.Scenarios)
        {
            foreach (var variant in scenario.Variants)
            {
                var proposals = variant.Items.Select(i => new InboxProposal
                {
                    Kind = i.Kinds[0], Title = "Review interpretation", Description = null,
                    Properties = i.PropertySets[0], ProjectIds = i.ProjectIdSets[0],
                    Ambiguities = i.RequireAmbiguity ? ["Review"] : []
                }).ToArray();
                var snapshot = Snapshot(proposals: proposals, original: scenario.Text,
                    capturedAt: scenario.CapturedAt, context: scenario.Context);
                var result = Evaluate(snapshot);
                Assert.NotEqual(InboxPolicyRoute.AutoApply, result.Route);
                if (proposals.Length > 1 || proposals.Any(p => p.Properties.UnresolvedDateText is not null ||
                    p.Kind == InboxTargetKind.WaitingFor && p.Properties.WaitingSince is null || p.ProjectIds.Length > 1))
                {
                    Assert.Equal(InboxPolicyRoute.KeepInInbox, result.Route);
                }
            }
        }

        // Reproduce structurally valid M3 failures, even when the model reports no ambiguity.
        var waiting = dataset.Scenarios.Single(s => s.Id == "waiting-09");
        var wrongSource = Snapshot(proposals: [Proposal(InboxTargetKind.WaitingFor) with
        {
            Properties = Empty with { Source = "восстановят электричество" }
        }], original: waiting.Text, capturedAt: waiting.CapturedAt, context: waiting.Context);
        Assert.Contains(InboxPolicyReason.WaitingSourceRequiresReview, Evaluate(wrongSource).Reasons);
        var edge = dataset.Scenarios.Single(s => s.Id == "edge-10");
        var wrongKind = Snapshot(proposals: [Proposal()], original: edge.Text, capturedAt: edge.CapturedAt, context: edge.Context);
        Assert.Contains(InboxPolicyReason.ActionReferenceBoundaryRequiresReview, Evaluate(wrongKind).Reasons);
    }

    private static InboxPolicyDecision Evaluate(InboxPolicySnapshot snapshot) =>
        new InboxPolicyEngine().Evaluate(Owner, 7, snapshot.Analysis.Id, snapshot, InboxPolicyEngine.LegacyVersion);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EvaluateV2_AllKindsIncludingAmbiguitiesAndExtraDescription_AutoApplies(InboxTargetKind kind, bool ambiguous)
    {
        var snapshot = Snapshot(Proposal(kind) with
        {
            Description = "Extra detail preserved in audit", Ambiguities = ambiguous ? ["Uncertain interpretation"] : [],
            Properties = Empty
        });
        var result = EvaluateV2(snapshot);
        Assert.Equal(InboxPolicyRoute.AutoApply, result.Route);
        Assert.Equal(InboxPolicyRoute.AutoApply, Assert.Single(result.Proposals).Route);
        Assert.Equal("inbox-policy-v2", result.PolicyVersion);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("current")]
    [InlineData("ambiguous")]
    [InlineData("stale")]
    [InlineData("truncated")]
    public void EvaluateV2_OptionalProjectContext_DoesNotPreventCreation(string scenario)
    {
        var proposal = Proposal() with
        {
            ProjectIds = scenario switch { "current" or "stale" => [1], "ambiguous" => [1, 2], _ => [] },
            Ambiguities = ["Context uncertain"], Properties = Empty with { UnresolvedDateText = "завтра" }
        };
        var snapshot = Snapshot(proposal, context: new(null,
            [new(1, "Project", CapturedAt), new(2, "Other", CapturedAt)], scenario == "truncated"));
        if (scenario == "stale")
        {
            snapshot = snapshot with { ActiveOwnedProjects = [] };
        }
        Assert.Equal(InboxPolicyRoute.AutoApply, EvaluateV2(snapshot).Route);
    }

    [Fact]
    public void EvaluateV2_Composite_AutoAppliesAllOrBlocksAll()
    {
        var snapshot = Snapshot(proposals: [Proposal(), Proposal(), Proposal(InboxTargetKind.SomedayMaybe)]);
        Assert.Equal(InboxPolicyRoute.AutoApply, EvaluateV2(snapshot).Route);
        snapshot = Snapshot(proposals: [Proposal(), Proposal() with { Title = " " }]);
        Assert.Equal(InboxPolicyRoute.KeepInInbox, EvaluateV2(snapshot).Route);
        Assert.Contains(InboxPolicyReason.InvalidProposal, EvaluateV2(snapshot).Reasons);
    }

    [Fact]
    public void EvaluateV2_StaleInputAnalysisAndInactiveInbox_FailsClosed()
    {
        var snapshot = Snapshot(Proposal());
        Assert.Contains(InboxPolicyReason.AnalysisSuperseded, EvaluateV2(snapshot with { LatestAnalysisVersion = 2 }).Reasons);
        Assert.Contains(InboxPolicyReason.InputChanged, EvaluateV2(Snapshot(Proposal(), inputHash: "old")).Reasons);
        snapshot.Item.Discard(CapturedAt);
        Assert.Contains(InboxPolicyReason.InboxInactive, EvaluateV2(snapshot).Reasons);
        Assert.Contains(InboxPolicyReason.InvalidContext, EvaluateV2(Snapshot(Proposal(), contextJson: "{}")).Reasons);
    }

    private static InboxPolicyDecision EvaluateV2(InboxPolicySnapshot snapshot) =>
        new InboxPolicyEngine().Evaluate(Owner, 7, snapshot.Analysis.Id, snapshot, InboxPolicyEngine.Version);

    private static InboxProposal Proposal(InboxTargetKind kind = InboxTargetKind.Action) => new()
    {
        Kind = kind, Title = "Review", Description = null, ProjectIds = [], Ambiguities = [],
        Properties = kind == InboxTargetKind.WaitingFor ? Empty with
        {
            Source = "Ивана", WaitingSince = DateTimeOffset.Parse("2026-10-20T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            WaitingSinceEvidence = "2026-10-20T09:00:00Z"
        } : Empty
    };

    private static InboxPolicySnapshot Snapshot(InboxProposal? first = null, InboxProposal? second = null,
        InboxAnalysisStatus status = InboxAnalysisStatus.Completed, string? inputHash = null, string? contextJson = null,
        string schema = "inbox-v1", InboxProposal[]? proposals = null, string original = Original,
        DateTime? capturedAt = null, InboxAnalysisContext? context = null)
    {
        var time = capturedAt ?? CapturedAt;
        var item = InboxItem.Capture(Owner, original, time);
        typeof(InboxItem).GetProperty(nameof(InboxItem.Id))!.SetValue(item, 7L);
        context ??= new(null, [new(1, "Project", CapturedAt), new(2, "Other", CapturedAt)], false);
        var json = contextJson ?? JsonSerializer.Serialize(context, InboxAnalysisJson.Options);
        var analysis = InboxAnalysis.Start(Owner, 7, 1, new("model", schema, "prompt"), json, Hash(json), inputHash ?? Hash(original), time);
        if (status != InboxAnalysisStatus.Pending)
        {
            analysis.Finish(status, time, "{}", status == InboxAnalysisStatus.Completed
                ? new() { Items = proposals ?? new[] { first, second }.OfType<InboxProposal>().ToArray() } : null);
        }
        return new(item, analysis, 1, context.Projects);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
