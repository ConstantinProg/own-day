# Automatic Inbox processing

Telegram result notifications and the compatibility confirmation route are now implemented in
[Telegram feedback and confirmation](Phase4-Milestone6-Telegram-Feedback.md).

The application now uses **`inbox-policy-v2`**. This is a personal experiment: a structurally valid AI
interpretation may be semantically wrong and still be applied. There are no confidence scores, profiles,
semantic review gates or new policy configuration. The original text and proposals remain available for correction.

## Policy and mapping

`InboxPolicyEngine.Version` identifies v2. `LegacyVersion` identifies the unchanged v1 rules, available only
through explicit versioned evaluation. `InboxPolicyService.EvaluateAsync` without a version evaluates v2.
Unknown versions fail with `PolicyVersionUnsupported`. Neither evaluation path calls the LLM.

V2 accepts 1–20 independently valid proposals as **one atomic batch**. Every proposal and the aggregate
receive AutoApply when the existing structural and ownership checks pass. Semantic uncertainty, Ambiguities,
multiple proposals, additional descriptions and truncated project context do not prevent creation.

| Saved proposal | Applied domain data |
| --- | --- |
| Action | Canonical title → Action.Title |
| Project | Canonical title → Project.Title; no parent project |
| SomedayMaybe | Canonical title → SomedayMaybe.Text |
| Reference | Canonical title → Reference.Text |
| WaitingFor | Canonical title → WaitingFor.Description; validated source → Source; explicit validated UTC start → WaitingSince |
| WaitingFor with no start | Existing processing use case defaults WaitingSince to processing time; this is a technical default, not a claim about the historical start |
| Exactly one current, owned, active, unchanged project candidate | Apply that project link |
| Multiple project candidates | Create without a project, even if only one candidate remains active; do not resolve the original alternatives arbitrarily |
| No candidate, or candidate deleted/completed/transferred/renamed since analysis | Create without a project; links are optional in all current supporting types |
| Truncated project context | Create normally; never infer a missing project or link |
| Additional description | Keep the canonical title as the sole target text, without concatenating or truncating. Preserve the complete description in the saved proposal and audit |
| Unresolved dates | Preserve verbatim in analysis/audit; do not set a deadline or invent a date |

All current target text fields have a 200-character limit. Even WaitingFor has only one primary description
field, so it uses the canonical title under the same rule. No current target has a second long-description
field. The minimal mapping does not lose the original source or AI description, although target lists only
show the primary text. The existing analysis schema stays `inbox-v1`; unsupported arbitrary JSON fields are
still rejected structurally. No deadline, priority or other unsupported domain property is invented.

**RequireConfirmation:** no v2 scenario in the current contract requires it. All required target data is
already validated, and project links/source are optional while waiting start has an existing default.
The route remains for v1 compatibility and situations that actually require a user choice. Telegram now handles
that route with confirmation buttons; no new mandatory choices are introduced in v2. Ambiguity in interpretation
alone is not such a situation.

**KeepInInbox:** missing/foreign or inactive Inbox, incomplete/failed/invalid/superseded analysis, changed input,
unsupported schema/version, corrupt or unavailable context, invalid proposal set/payload or domain invariants.
One invalid proposal blocks the whole batch. Global guards and the existing M1 validator run again before
application. An inactive item is not reactivated. Pending analyses are left unassessed for later completion.
Storage failures roll back the transaction and remain pending application for retry, rather than pretending
that a result was committed. A new analysis version is needed to reconsider a persisted terminal assessment.

## Application, atomicity and concurrency

`InboxAutomaticProcessingService` coordinates the existing `IInboxProcessingStore` lock/transaction and
`InboxProcessingService` target creation. Domain construction and transitions remain in Domain. The manual
single-target processing path uses the same construction and stores, with its existing validations and defaults.

Policy evaluation retains the validated proposals and context long enough to prepare typed commands without
parsing them again. Each command carries its proposal UUID through target creation into the audit mapping.
Single-target and batch processing explicitly finalize the Inbox after shared target creation; target creation
alone does not report the Inbox as processed. The v1 and v2 proposal rules and aggregation remain separate.

1. Select a terminal saved analysis with no application record. This query only discovers work; it grants no permission.
2. Acquire the owner-qualified Inbox row using the existing PostgreSQL `FOR UPDATE` lock.
3. Return an existing application record for the same analysis if present.
4. Reload analysis/proposals, latest version and current project data inside the transaction. Analysis version
   allocation also takes this Inbox lock, so a newer attempt cannot appear between the check and commit.
5. Hold `FOR SHARE` locks on existing active context projects, ordered by ID, until commit. This prevents completion,
   rename, owner transfer or deletion of a selected project between validation and target insertion. Missing or
   stale optional links are omitted. All reads are owner-qualified and bypass tracked historical project state.
6. Evaluate v2 from this snapshot. Create all targets through the shared processing code and only then call
   `InboxItem.ProcessMany`. Persist all target IDs and the application audit in the same transaction.
7. Commit. An exception or cancellation before commit rolls back every target, the Inbox transition and audit.

The unique application primary key is `analysis_id`. Proposal UUIDs/positions are validated and the audit maps
every proposal to exactly one created kind/ID/project ID. There is no partial proposal application, so a single
analysis identity plus the Inbox lock fences every proposal. Different analyses of the same Inbox cannot both
create targets: the winner changes the Inbox to Processed. Manual processing/discard uses that same lock;
if it wins, automatic processing records KeepInInbox without replacing the manual result.

`InboxItem.Targets` contains every created kind/ID. The existing `TargetKind`/`TargetId` fields continue to
identify the **first** target for compatibility; callers needing the complete result must use `Targets` or
the application audit. The existing lifecycle constraint remains valid. Manual processing creates one collection
entry. Migration backfills collection entries for previously processed Inbox items.

## Background processing and recovery

The existing `InboxAnalysisWorker` now runs one application polling loop alongside its existing analysis
and maintenance loops. It uses the existing `InboxAnalysis:Background:PollSeconds` setting and a fresh DI
scope/DbContext for each application attempt. There is no additional worker type, universal workflow engine,
broker, LLM call or webhook processing path.

Terminal saved analyses form the durable source of work: absence of an application row means evaluation/application
is pending. Analysis completion is committed first; application uses its own short transaction. A crash in between
is recovered by polling on the next start. A crash during application rolls back all writes; a crash after commit,
including lost commit acknowledgement, finds the existing audit and does not create targets again.

The application loop requires no provider key and continues applying already saved results when analysis/provider
is disabled. Existing Enabled flags govern new LLM work. On upgrade, **existing terminal analyses are assessed too**;
only the newest structurally valid analysis of an active Inbox can create targets. Older analyses, failures and
inactive Inbox items receive a terminal KeepInInbox audit. No historical proposal is relabeled as v2 output.

Transient database failures/cancellation leave the saved analysis unconsumed. The worker disposes the failed scope,
logs only the exception type and retries on the existing polling interval. No new paid AI call is needed for an
application retry. Direct callers should also dispose a failed scope before retrying, as with the existing EF stores.
At this personal-use scale discovery chooses the oldest outstanding analysis, and application is sequential per Host;
multiple Hosts remain safe under the database locks. A persistent storage failure on that oldest analysis can delay
later work until the storage problem is fixed. There is no new retry-budget/configuration subsystem.

The Telegram webhook captures/schedules analysis and handles manual commands and confirmation callbacks. Existing update
deduplication, draft concurrency and transactional reply outbox remain in use. Automatic application now enlists
one result notification in its Inbox processing transaction for entries captured through Telegram.
A draft whose item was automatically processed
follows the existing unavailable/processing-unavailable behavior when the user continues or confirms it.

## Persistence and future Undo

Migration `20261025155940_AddInboxAutomaticProcessing` adds:

- `inbox_targets`: owned child rows with Inbox FK, kind/ID composite identity and a positive ID/supported-kind constraint;
  it backfills existing processed items from legacy fields.
- `inbox_automatic_applications`: one immutable outcome per analysis, owner-qualified Inbox reference, policy version,
  UTC evaluation/application completion time, route, complete decision including state fingerprint, original
  proposal snapshots with IDs/payloads, and created target mappings with proposal UUID and effective project ID.

The source Inbox and analysis/proposals are retained. Restrict FKs preserve source/history references. The record's
time is written after successful target creation and committed with the result. A failed transaction has no committed
application audit; its preserved source/analysis is the recovery marker. This is enough to identify what a future
Undo would reverse. Undo, editing commands and retrospective semantic correction are outside this change.

Apply the migration before starting the updated Host, using normal runtime connection configuration:

```powershell
dotnet ef database update --project src/OwnDay.Infrastructure --startup-project src/OwnDay.Infrastructure
```

No package, model, prompt, secret or production configuration changes are required.

## Examples

The following are expected mappings when analysis returns the corresponding structurally valid proposals;
LLM classification itself remains probabilistic.

| Source entry | Automatic result |
| --- | --- |
| «Купить молоко» | Action «Купить молоко» |
| «Организовать переезд в новую квартиру» | Project «Организовать переезд в новую квартиру» |
| «Жду от Ивана договор» | WaitingFor «Получить договор», Source «Ивана», start defaults to processing time |
| «Пароль от гостевого Wi-Fi указан на роутере» | Reference with the canonical factual text |
| «Когда-нибудь попробовать изучить японский» | SomedayMaybe «Попробовать изучить японский» |
| «Купить молоко, позвонить Ивану и когда-нибудь попробовать изучить японский» | Two Actions and one SomedayMaybe, one atomic commit |
| «Завтра позвонить мастеру» | Action with the canonical title; unresolved «Завтра» remains in analysis/audit, without an invented deadline |
| Valid action with two candidate projects | Action without a project; both candidates remain in the original proposal |

## Verification

Unit tests cover v1 compatibility, v2 for all five types with/without ambiguity and extra descriptions, default
waiting date eligibility, unresolved dates, optional/stale/truncated project context, composite aggregation,
invalid input/history and Domain multiple-target invariants.

PostgreSQL tests use real migrations in isolated GUID schemas: all five target kinds, original/audit retention,
composites, effective project mapping, missing waiting start, repeated application after restart, four concurrent
workers, manual-vs-automatic locking, concurrent project mutation, rollback on intermediate insert/audit/commit
failure, lost commit acknowledgement, superseded/changed/inactive Inbox, cancellation/ownership, migration backfill,
and hosted processing from capture through AI completion to automatic application with exactly one fake provider call.
Restart recovery of saved results is tested with provider disabled and zero provider calls. Existing manual Telegram,
outbox and M1–M4 suites run alongside these tests. Ordinary tests never contact OpenAI.

```powershell
# Set OWNDAY_TEST_POSTGRES_CONNECTION to a test PostgreSQL database first.
dotnet restore OwnDay.slnx
dotnet build OwnDay.slnx --configuration Release --no-restore
dotnet test OwnDay.slnx --configuration Release --no-build -m:1
```
