using System.Diagnostics.CodeAnalysis;
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
    public const string LegacyVersion = "inbox-policy-v1";
    public const string Version = "inbox-policy-v2";

    public InboxPolicyDecision Evaluate(UserId userId, long itemId, Guid analysisId,
        InboxPolicySnapshot? snapshot, string policyVersion)
        => EvaluateCore(userId, itemId, analysisId, snapshot, policyVersion).Decision;

    internal InboxApplicationPreparation PrepareApplication(UserId userId, long itemId, Guid analysisId,
        InboxPolicySnapshot snapshot, string policyVersion = Version)
        => EvaluateCore(userId, itemId, analysisId, snapshot, policyVersion);

    private static InboxApplicationPreparation EvaluateCore(UserId userId, long itemId, Guid analysisId,
        InboxPolicySnapshot? snapshot, string policyVersion)
    {
        var reasons = new List<InboxPolicyReason>();
        var decisions = new List<ProposalPolicyDecision>();
        var prepared = new List<PreparedInboxProposal>();
        var fingerprint = snapshot is null ? string.Empty : ComputeStateFingerprint(snapshot);

        InboxApplicationPreparation Result(InboxPolicyRoute route) => new(
            new(policyVersion, analysisId, itemId, snapshot?.Analysis.Version, fingerprint, route,
                Array.AsReadOnly(reasons.Distinct().ToArray()), decisions.AsReadOnly()),
            route != InboxPolicyRoute.KeepInInbox ? prepared.AsReadOnly() : Array.Empty<PreparedInboxProposal>());
        InboxApplicationPreparation Block(InboxPolicyReason reason)
        {
            reasons.Add(reason);
            return Result(InboxPolicyRoute.KeepInInbox);
        }

        if (policyVersion != LegacyVersion && policyVersion != Version)
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
        if (item.ManualProcessingOnly)
        {
            return Block(InboxPolicyReason.ManualProcessingOnly);
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

        if (!TryReadValidatedContext(analysis, out var context))
        {
            return Block(InboxPolicyReason.InvalidContext);
        }
        var rows = analysis.Proposals.OrderBy(p => p.Position).ToArray();
        if (!ValidateProposalSet(rows, analysisId))
        {
            return Block(InboxPolicyReason.InvalidProposalSet);
        }

        var request = new InboxAnalysisRequest(item.OriginalText, item.CapturedAt, context, InboxAnalysisService.SupportedTypes);
        var validations = ValidateProposals(rows, request);
        if (policyVersion == Version)
        {
            decisions.AddRange(EvaluateV2Proposals(validations));
            var route = AggregateV2(decisions, reasons);
            if (route == InboxPolicyRoute.AutoApply)
            {
                foreach (var validation in validations)
                {
                    if (validation.Proposal is { } proposal)
                    {
                        prepared.Add(new(validation.Row.Id, ToCommand(itemId, proposal, context, currentProjects)));
                    }
                }
            }
            return Result(route);
        }

        decisions.AddRange(EvaluateV1Proposals(validations, context, currentProjects));
        var legacyRoute = AggregateV1(decisions, reasons);
        if (legacyRoute == InboxPolicyRoute.RequireConfirmation)
        {
            foreach (var validation in validations)
            {
                if (validation.Proposal is { } proposal)
                {
                    prepared.Add(new(validation.Row.Id, ToCommand(itemId, proposal, context, currentProjects)));
                }
            }
        }
        return Result(legacyRoute);
    }

    private static IReadOnlyList<ProposalValidation> ValidateProposals(InboxAnalysisProposal[] rows,
        InboxAnalysisRequest request)
    {
        var validator = new InboxAnalysisValidator();
        var validations = new List<ProposalValidation>();
        foreach (var row in rows)
        {
            var proposal = validator.ParseAndValidateProposal(row.PayloadJson, request);
            validations.Add(new(row, proposal is not null && proposal.Kind == row.Kind && proposal.Title == row.Title
                ? proposal : null));
        }
        return validations.AsReadOnly();
    }

    private static IEnumerable<ProposalPolicyDecision> EvaluateV2Proposals(IReadOnlyList<ProposalValidation> proposals) =>
        proposals.Select(p => p.Proposal is null ? InvalidProposal(p.Row)
            : new ProposalPolicyDecision(p.Row.Id, p.Row.Position, p.Row.Kind,
                InboxPolicyRoute.AutoApply, Array.Empty<InboxPolicyReason>()));

    private static IEnumerable<ProposalPolicyDecision> EvaluateV1Proposals(IReadOnlyList<ProposalValidation> proposals,
        InboxAnalysisContext context, IReadOnlyList<AnalysisProject> currentProjects) =>
        proposals.Select(p => p.Proposal is { } proposal
            ? EvaluateV1Proposal(p.Row, proposal, context, currentProjects) : InvalidProposal(p.Row));

    private static ProposalPolicyDecision InvalidProposal(InboxAnalysisProposal row) =>
        new(row.Id, row.Position, row.Kind, InboxPolicyRoute.KeepInInbox,
            Array.AsReadOnly(new[] { InboxPolicyReason.InvalidProposal }));

    private static InboxPolicyRoute AggregateV2(IReadOnlyList<ProposalPolicyDecision> decisions,
        List<InboxPolicyReason> reasons)
    {
        AddProposalReasons(decisions, reasons);
        return decisions.All(p => p.Route == InboxPolicyRoute.AutoApply)
            ? InboxPolicyRoute.AutoApply : InboxPolicyRoute.KeepInInbox;
    }

    private static InboxPolicyRoute AggregateV1(IReadOnlyList<ProposalPolicyDecision> decisions,
        List<InboxPolicyReason> reasons)
    {
        if (decisions.Count > 1)
        {
            reasons.Add(InboxPolicyReason.CompositeRequiresJointReview);
            // Preserve the historical single-target restriction when replaying v1.
            reasons.Add(InboxPolicyReason.MultipleTargetsNotSupported);
        }
        AddProposalReasons(decisions, reasons);
        return decisions.Count > 1 || decisions.Any(p => p.Route == InboxPolicyRoute.KeepInInbox)
            ? InboxPolicyRoute.KeepInInbox : InboxPolicyRoute.RequireConfirmation;
    }

    private static void AddProposalReasons(IReadOnlyList<ProposalPolicyDecision> decisions,
        List<InboxPolicyReason> reasons)
    {
        if (decisions.Any(p => p.Route == InboxPolicyRoute.KeepInInbox))
        {
            reasons.Add(InboxPolicyReason.ProposalBlocked);
        }
        reasons.AddRange(decisions.SelectMany(p => p.Reasons));
    }

    private static ProcessInboxItem ToCommand(long itemId, InboxProposal proposal,
        InboxAnalysisContext context, IReadOnlyList<AnalysisProject> currentProjects)
    {
        // Project candidates are alternatives. Drop an optional link if it is ambiguous, stale or unavailable.
        long? projectId = proposal.ProjectIds.Length == 1 && currentProjects.Any(p =>
            p.Id == proposal.ProjectIds[0] && context.Projects.Contains(p)) ? proposal.ProjectIds[0] : null;
        // Keep the canonical title; extra descriptions and unresolved dates remain in analysis and audit.
        return new(itemId, proposal.Kind, proposal.Title, projectId,
            proposal.Properties.Source, proposal.Properties.WaitingSince?.UtcDateTime);
    }

    private sealed record ProposalValidation(InboxAnalysisProposal Row, InboxProposal? Proposal);

    private static string ComputeStateFingerprint(InboxPolicySnapshot snapshot)
    {
        return Fingerprint(JsonSerializer.Serialize(new
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
    }

    private static bool TryReadValidatedContext(InboxAnalysis analysis,
        [NotNullWhen(true)] out InboxAnalysisContext? context)
    {
        try
        {
            context = JsonSerializer.Deserialize<InboxAnalysisContext>(analysis.ContextJson, InboxAnalysisJson.Options);
        }
        catch (JsonException)
        {
            context = null;
            return false;
        }

        return context?.Projects is not null && context.Projects.Count <= InboxAnalysisService.MaxProjects &&
            context.Projects.All(p => p is not null && p.Id > 0 && !string.IsNullOrWhiteSpace(p.Title) &&
                p.Title.Length <= Domain.Projects.Project.MaxTitleLength && p.CreatedAt.Kind == DateTimeKind.Utc) &&
            context.Projects.Select(p => p.Id).Distinct().Count() == context.Projects.Count &&
            // PostgreSQL jsonb normalizes whitespace and key order. Analysis hashes the typed serialization.
            analysis.ContextFingerprint == Fingerprint(JsonSerializer.Serialize(context, InboxAnalysisJson.Options));
    }

    private static bool ValidateProposalSet(InboxAnalysisProposal[] rows, Guid analysisId) =>
        rows.Length is >= 1 and <= 20 && rows.Select(p => p.Id).Distinct().Count() == rows.Length &&
        !rows.Where((p, index) => p.Id == Guid.Empty || p.AnalysisId != analysisId || p.Position != index).Any();

    private static ProposalPolicyDecision EvaluateV1Proposal(InboxAnalysisProposal row, InboxProposal proposal,
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
