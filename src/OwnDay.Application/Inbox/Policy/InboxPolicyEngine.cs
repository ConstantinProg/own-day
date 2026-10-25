using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OwnDay.Application.Inbox.Analysis;
using OwnDay.Domain;
using OwnDay.Domain.Inbox;

namespace OwnDay.Application.Inbox.Policy;

// Pure workflow policy: no clock, I/O, provider, domain mutation or configurable confidence threshold.
public sealed class InboxPolicyEngine
{
    public const string Version = "inbox-policy-v1";

    public InboxPolicyDecision Evaluate(UserId userId, long itemId, Guid analysisId,
        InboxPolicySnapshot? snapshot, string policyVersion)
    {
        var reasons = new List<InboxPolicyReason>();
        var decisions = new List<ProposalPolicyDecision>();
        var fingerprint = snapshot is null ? string.Empty : Fingerprint(JsonSerializer.Serialize(new
        {
            snapshot.Item.Id, snapshot.Item.UserId, snapshot.Item.OriginalText, snapshot.Item.CapturedAt,
            snapshot.Item.Status, snapshot.LatestAnalysisVersion,
            analysis = new
            {
                snapshot.Analysis.Id, snapshot.Analysis.UserId, snapshot.Analysis.InboxItemId,
                snapshot.Analysis.Version, snapshot.Analysis.Status, snapshot.Analysis.SchemaVersion,
                snapshot.Analysis.InputFingerprint, snapshot.Analysis.ContextFingerprint, snapshot.Analysis.ContextJson,
                proposals = snapshot.Analysis.Proposals.OrderBy(p => p.Position).Select(p => new
                {
                    p.Id, p.AnalysisId, p.Position, kind = p.Kind.ToString(), p.Title, p.PayloadJson
                })
            },
            projects = snapshot.ActiveOwnedProjects.OrderBy(p => p.Id)
        }, InboxAnalysisJson.Options));

        InboxPolicyDecision Result(InboxPolicyRoute route) => new(policyVersion, analysisId, itemId,
            snapshot?.Analysis.Version, fingerprint, route, Array.AsReadOnly(reasons.Distinct().ToArray()),
            decisions.AsReadOnly());
        InboxPolicyDecision Block(InboxPolicyReason reason)
        {
            reasons.Add(reason);
            return Result(InboxPolicyRoute.KeepInInbox);
        }

        if (policyVersion != Version)
        {
            return Block(InboxPolicyReason.PolicyVersionUnsupported);
        }
        if (snapshot is null)
        {
            return Block(InboxPolicyReason.AnalysisUnavailable);
        }

        var (item, analysis, latestVersion, currentProjects) = snapshot;
        if (!userId.IsValid || item.UserId != userId || analysis.UserId != userId ||
            item.Id != itemId || analysis.InboxItemId != itemId || analysis.Id != analysisId)
        {
            return Block(InboxPolicyReason.OwnershipMismatch);
        }
        if (item.Status != InboxItemStatus.Active)
        {
            return Block(InboxPolicyReason.InboxInactive);
        }
        if (analysis.Status != InboxAnalysisStatus.Completed)
        {
            return Block(InboxPolicyReason.AnalysisNotCompleted);
        }
        if (analysis.Version != latestVersion)
        {
            return Block(InboxPolicyReason.AnalysisSuperseded);
        }
        if (analysis.SchemaVersion != "inbox-v1")
        {
            return Block(InboxPolicyReason.SchemaUnsupported);
        }
        if (analysis.InputFingerprint != Fingerprint(item.OriginalText))
        {
            return Block(InboxPolicyReason.InputChanged);
        }

        InboxAnalysisContext? context;
        try
        {
            context = JsonSerializer.Deserialize<InboxAnalysisContext>(analysis.ContextJson, InboxAnalysisJson.Options);
        }
        catch (JsonException)
        {
            return Block(InboxPolicyReason.InvalidContext);
        }
        if (context?.Projects is null || context.Projects.Count > InboxAnalysisService.MaxProjects ||
            context.Projects.Any(p => p is null || p.Id <= 0 || string.IsNullOrWhiteSpace(p.Title) ||
                p.Title.Length > Domain.Projects.Project.MaxTitleLength || p.CreatedAt.Kind != DateTimeKind.Utc) ||
            context.Projects.Select(p => p.Id).Distinct().Count() != context.Projects.Count ||
            // PostgreSQL jsonb normalizes whitespace and key order. M1 hashes the typed serialization.
            analysis.ContextFingerprint != Fingerprint(JsonSerializer.Serialize(context, InboxAnalysisJson.Options)))
        {
            return Block(InboxPolicyReason.InvalidContext);
        }
        var rows = analysis.Proposals.OrderBy(p => p.Position).ToArray();
        if (rows.Length is < 1 or > 20 || rows.Select(p => p.Id).Distinct().Count() != rows.Length ||
            rows.Where((p, index) => p.Id == Guid.Empty || p.AnalysisId != analysisId || p.Position != index).Any())
        {
            return Block(InboxPolicyReason.InvalidProposalSet);
        }

        var request = new InboxAnalysisRequest(item.OriginalText, item.CapturedAt, context, InboxAnalysisService.SupportedTypes);
        foreach (var row in rows)
        {
            // Reuse the independent M1 structural/value checks, including evidence and property compatibility.
            var output = new InboxAnalysisValidator().ParseAndValidate("{\"items\":[" + row.PayloadJson + "]}", request);
            var proposal = output?.Items is { Length: 1 } ? output.Items[0] : null;
            if (proposal is null || proposal.Kind != row.Kind || proposal.Title != row.Title)
            {
                decisions.Add(new(row.Id, row.Position, row.Kind, InboxPolicyRoute.KeepInInbox,
                    Array.AsReadOnly(new[] { InboxPolicyReason.InvalidProposal })));
                continue;
            }
            decisions.Add(EvaluateProposal(row, proposal, context, currentProjects));
        }

        if (rows.Length > 1)
        {
            reasons.Add(InboxPolicyReason.CompositeRequiresJointReview);
            // InboxItem.Process currently records exactly one target. Never process a subset of the batch.
            reasons.Add(InboxPolicyReason.MultipleTargetsNotSupported);
        }
        if (decisions.Any(p => p.Route == InboxPolicyRoute.KeepInInbox))
        {
            reasons.Add(InboxPolicyReason.ProposalBlocked);
        }
        reasons.AddRange(decisions.SelectMany(p => p.Reasons));
        return Result(rows.Length > 1 || decisions.Any(p => p.Route == InboxPolicyRoute.KeepInInbox)
            ? InboxPolicyRoute.KeepInInbox : InboxPolicyRoute.RequireConfirmation);
    }

    private static ProposalPolicyDecision EvaluateProposal(InboxAnalysisProposal row, InboxProposal proposal,
        InboxAnalysisContext context, IReadOnlyList<AnalysisProject> currentProjects)
    {
        var reasons = new List<InboxPolicyReason> { InboxPolicyReason.AutonomousApplicationNotValidated };
        var blocked = false;
        void Block(InboxPolicyReason code)
        {
            blocked = true;
            reasons.Add(code);
        }

        if (proposal.Description is not null && proposal.Description != proposal.Title)
        {
            Block(InboxPolicyReason.DescriptionNotSupported);
        }
        if (proposal.Properties.UnresolvedDateText is not null)
        {
            Block(InboxPolicyReason.UnresolvedDateNotSupported);
        }
        if (proposal.ProjectIds.Length > 1)
        {
            Block(InboxPolicyReason.ProjectChoiceRequired);
        }
        foreach (var id in proposal.ProjectIds)
        {
            var current = currentProjects.SingleOrDefault(p => p.Id == id);
            if (current is null)
            {
                Block(InboxPolicyReason.ProjectUnavailable);
            }
            else if (current != context.Projects.Single(p => p.Id == id))
            {
                Block(InboxPolicyReason.ProjectChanged);
            }
        }
        if (proposal.ProjectIds.Length > 0)
        {
            reasons.Add(InboxPolicyReason.ProjectLinkRequiresReview);
        }
        if (context.ProjectsTruncated)
        {
            reasons.Add(InboxPolicyReason.ContextTruncated);
        }
        if (proposal.Ambiguities.Length > 0)
        {
            reasons.Add(InboxPolicyReason.AmbiguityRequiresReview);
        }
        if (proposal.Kind == InboxTargetKind.WaitingFor)
        {
            // Domain requires a real waiting instant; do not silently substitute processing time for unknown data.
            if (proposal.Properties.WaitingSince is null)
            {
                Block(InboxPolicyReason.WaitingStartRequired);
            }
            reasons.Add(InboxPolicyReason.WaitingSourceRequiresReview);
            reasons.Add(InboxPolicyReason.WaitingDateRoleRequiresReview);
        }
        reasons.Add(proposal.Kind is InboxTargetKind.Action or InboxTargetKind.Reference
            ? InboxPolicyReason.ActionReferenceBoundaryRequiresReview : InboxPolicyReason.SemanticReviewRequired);
        return new(row.Id, row.Position, row.Kind,
            blocked ? InboxPolicyRoute.KeepInInbox : InboxPolicyRoute.RequireConfirmation,
            reasons.AsReadOnly());
    }

    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
