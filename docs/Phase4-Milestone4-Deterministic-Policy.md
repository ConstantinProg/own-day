# Deterministic Inbox policy

Implemented policy version: **`inbox-policy-v1`**. This document describes the implementation against
the existing M1–M3 contracts and current Domain, using the M4 requirements supplied for this change.
The repository has no separate overarching Phase 4 specification.

## API and authority

`InboxPolicyService.EvaluateAsync(userId, inboxItemId, analysisId, policyVersion, cancellationToken)`
reads a saved analysis and returns an `InboxPolicyDecision`. The caller supplies a trusted authenticated
identity, as with existing Application services. Resolve the scoped service through Host DI; it requires
no AI configuration, key or provider. No endpoint, worker dispatch or Telegram confirmation flow is added.

`InboxPolicyEngine.Evaluate` is the pure synchronous core. It has no I/O, clock, random numbers, model,
configuration thresholds or mutable instance state. It consumes the existing saved analysis/proposals
and a current detached snapshot. It reuses the M1 structural validator, rather than interpreting model
explanations as commands or duplicating type/property validation.

Results include the requested policy version, analysis UUID/version, Inbox ID, a state SHA-256 fingerprint,
an aggregate route, stable machine-readable enum reason codes and per-proposal UUID/position/kind/route/reasons.
Reasons are de-duplicated in stable rule order. Serialize enums as names using `InboxAnalysisJson.Options`.
Unknown versions return `KeepInInbox / PolicyVersionUnsupported`; they never fall back to current rules.

**Only the aggregate route describes the whole batch.** A proposal route is its standalone assessment,
not permission to apply it independently. Every composite entry is blocked as a whole in this version.
`RequireConfirmation` identifies an eligible review candidate; it does not record user consent or claim
semantic correctness. `KeepInInbox` on an inactive Inbox is a no-operation routing result, not a command to reactivate it.

## Rule table

Rules run in the following order. Global failures return no proposal assessments. Proposal failures retain
their own explanations and propagate `ProposalBlocked` and the specific reasons to the aggregate result.

| Check | Route | Reason codes |
| --- | --- | --- |
| Unrecognized explicit policy version | KeepInInbox | PolicyVersionUnsupported |
| Missing/foreign Inbox or analysis, or wrong Inbox/analysis pairing | KeepInInbox | AnalysisUnavailable (read boundary); OwnershipMismatch (pure core defense) |
| Inbox processed/discarded | KeepInInbox | InboxInactive |
| Analysis Pending, Failed or InvalidResponse | KeepInInbox | AnalysisNotCompleted |
| Any newer analysis attempt exists, including Pending/failed | KeepInInbox | AnalysisSuperseded |
| Unknown saved schema | KeepInInbox | SchemaUnsupported |
| Original text does not match analysis input hash | KeepInInbox | InputChanged |
| Malformed context, invalid project identities or context hash mismatch | KeepInInbox | InvalidContext |
| No proposals, over 20, duplicate UUIDs, invalid ordinals or analysis references | KeepInInbox | InvalidProposalSet |
| Invalid payload/type/title/properties, excerpt/date evidence or historical project candidates; payload disagrees with row metadata | KeepInInbox | InvalidProposal |
| Additional description differs from the title | KeepInInbox | DescriptionNotSupported |
| Unresolved date metadata, regardless of type | KeepInInbox | UnresolvedDateNotSupported |
| More than one candidate project, even if all are active | KeepInInbox | ProjectChoiceRequired |
| Referenced project deleted, completed or no longer owned | KeepInInbox | ProjectUnavailable |
| Referenced project title/creation identity differs from saved context | KeepInInbox | ProjectChanged |
| WaitingFor has no explicit validated waiting start | KeepInInbox | WaitingStartRequired |
| Candidate project is current | RequireConfirmation if otherwise representable | ProjectLinkRequiresReview |
| Context was truncated | RequireConfirmation if otherwise representable | ContextTruncated |
| Ambiguity list is nonempty | RequireConfirmation if otherwise representable | AmbiguityRequiresReview |
| WaitingFor with explicit start and exact source evidence (source may be null) | RequireConfirmation if otherwise representable | WaitingSourceRequiresReview, WaitingDateRoleRequiresReview |
| Action or Reference, even with no ambiguities | RequireConfirmation if otherwise representable | ActionReferenceBoundaryRequiresReview |
| Project or SomedayMaybe, even with no ambiguities | RequireConfirmation if otherwise representable | SemanticReviewRequired |
| Every structurally valid proposal | At least RequireConfirmation | AutonomousApplicationNotValidated |
| Two or more proposals, any mix of types/routes | KeepInInbox for entire analysis | CompositeRequiresJointReview, MultipleTargetsNotSupported |

Description equal to title is exact redundant data, so it does not require an additional Domain field.
No attempt is made to infer whether a different description is dispensable, concatenate it into the title,
truncate data, guess a date, pick one of several project alternatives or invent links between new proposals.

## Automatic application allowlist and denylist

**Allowed AutoApply cases: none in v1.** The enum reserves the route for a future explicitly versioned policy;
neither the engine nor its callers should infer an allowlist from the absence of blocking flags.

Automatic application is forbidden for all five types, singleton and composite entries, with or without
ambiguities, with current or absent project links, and with apparently complete properties. Validity is a
structural check; absence of model-reported ambiguity is not independent semantic evidence.

The [measured M3 results](Phase4-Milestone3-Evaluation-Results.md) have no independent human semantic review,
no held-out production distribution or repeated-trial safety evidence. Specifically:

- `edge-10`: Action/Reference classification can be wrong while passing structural validation. Both types require review.
- `edge-15`: oversplitting supporting information can change the resulting obligations. No subset of a batch may proceed.
- `waiting-09`: an exact excerpt can describe the expected event instead of its responsible source. WaitingFor always requires source-role review.
- `date-06`, `date-11`: valid excerpts do not establish date roles. Explicit instants still need review; unresolved dates cannot map to current targets.
- `reference-05`: missing ambiguity alongside unresolved date data is rejected again by the structural gate.

WaitingFor requires a non-null domain waiting start. Policy refuses to use the existing manual service's
processing-time default as evidence of when an actual wait began. Null source remains allowed by Domain but
is reviewed. Every target has one text/title field with a 200-character limit; there is no separate proposal
description or unresolved-date field. Inbox records only one target kind/ID. These are present storage limits,
not features implemented speculatively for later work.

## Architecture, consistency and versioning

Policy is Application workflow logic. Domain transitions remain in Domain; existing processing services,
Telegram deduplication/outbox, analysis jobs and provider execution are unchanged. The narrow `IInboxPolicyStore`
read port uses the existing DbContext/tables. Its Infrastructure adapter reads Inbox, analysis/proposals,
latest attempt and up to 50 context project IDs in a PostgreSQL **Repeatable Read** transaction.
Queries are owner-qualified and detached, filter current active projects, and do not depend on tracked values.
Existing explicit/ambient transactions are rejected to avoid silently inheriting weaker isolation. There is
no SaveChanges, target creation, outbox write, domain transition or update lock in policy evaluation.
Cancellation reaches every database operation. A concurrency test changes a project after the first query:
the current evaluation sees one snapshot and the next evaluation sees the new state.

Context fingerprints are verified using M1's typed canonical serialization; PostgreSQL jsonb changes raw key
order/whitespace. The decision state fingerprint covers the Inbox text/state/capture time, saved analysis
identity/schema/input/context, ordered proposal identities/payloads, latest analysis version and current
context projects. It binds an assessment to observed state, not to a wall-clock timeout.

Every evaluation requires an explicit version. Identical version/snapshot produces identical serialized
decisions. Re-evaluation returns a new immutable value and never overwrites analysis or a previous value.
Changes to rule behavior or domain support require a new version identifier; preserve the v1 implementation
or reject requests for it explicitly rather than relabeling it. There is no configurable confidence score.

This read-only milestone does **not persist policy decisions**, add an audit table or schedule their evaluation.
A caller retaining a decision must retain its complete versioned value, not later reconstruct it as if it had
been evaluated under new rules. The decision contract can be retained/serialized without changing the analysis
schema. A durable application/confirmation workflow is later work; it must revalidate ownership, input,
proposal IDs, policy version and current projects under its mutation transaction and record the decision it
actually acts on. A successful assessment is not a concurrency fence or durable authorization token.

No personal source text, titles, model explanations or payloads are emitted by new diagnostic logging.
The policy adds no logs. Safe consumers can report IDs/version/routes/reason codes; the hash is an opaque
state identifier, not anonymization of the underlying private source data.

## Validation

Table-driven unit tests exercise all five kinds with/without ambiguities, supported and malformed data,
project alternatives/removal/change, inactive Inbox, terminal and pending failures, ownership/schema/context/
input/version checks, batch aggregation, immutability, repeatability and explicit policy version rejection.
All 115 Golden Dataset scenarios and their authored variants are exercised offline, with explicit M3
source/classification regressions. PostgreSQL tests exercise migrated jsonb round-trips, current project
ownership/status/deletion/rename, consistent concurrent reads, detached reads, no implicit SaveChanges,
newer attempts, cancellation and provider-free DI registration.

Run the standard repository checks with `OWNDAY_TEST_POSTGRES_CONNECTION` set to a test PostgreSQL database:

```powershell
dotnet restore OwnDay.slnx
dotnet build OwnDay.slnx --configuration Release --no-restore
dotnet test OwnDay.slnx --configuration Release --no-build -m:1
```

PostgreSQL tests use GUID-isolated schemas and real migrations. Ordinary checks do not call OpenAI;
the existing real-provider smoke test remains opt-in. No schema migration or production configuration change
is required for this read-only capability. Application of proposals and Telegram confirmation are not implemented.
