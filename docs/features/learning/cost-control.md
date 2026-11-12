# Shared provider budget

OwnDay's shared `openai_request_attempts` ledger meters Inbox classification and
each physical Learning planner, generator, assessor and explainer invocation.
Learning rows reference the technical attempt (and thereby its request/step),
while historical Inbox rows retain their analysis FK. A database check permits
exactly one source. Admission derives Learning ownership from the persisted
attempt; a unique FK prevents admitting that invocation twice. No separate
Learning financial ledger or billing data store is introduced.

## Approved policy and configuration

The approved service budget is **USD 10 per UTC calendar month**. Existing
`InboxAnalysis:CostControl` settings are retained and now apply to both sources.

| Setting | Runtime default | Meaning |
|---|---:|---|
| `MonthlyUsdLimit` | 10 | Shared monthly admission ceiling |
| `TokensPerRequest` | 100000 | Reservation per admitted physical call |
| `MonthlyTokenLimit` | 20000000 | Shared monthly accounted/reserved token cap |
| `MonthlyUserTokenLimit` | 2000000 | Per-user monthly token cap |
| `MonthlyCallLimit` | 10000 | Shared monthly admitted call cap |
| `MonthlyUserCallLimit` | 1000 | Per-user monthly admitted call cap |
| `TokenLimit` | null in appsettings | Optional legacy lifetime token ceiling |

Limits use PostgreSQL time and one common advisory budget lock. Admission and
reconciliation use independent short transactions without pedagogical/user locks
or provider I/O. When the next reservation would exceed a limit, no provider is
called. Each retry reserves separately. A retained step result needs no new call.
Monthly rollover selects a new UTC period without deleting history. Unknown usage
stays charged in its invocation's original period; it is never changed to zero.

The USD reservation uses the configured token reservation and the highest known
input/cached/output unit price. It is replaced with known actual usage cost only
after reconciliation. Missing usage or an unknown effective price retains the
reservation. An unpriced requested model is rejected. Legacy rows with neither
known cost nor a reservation in the current month block monetary admission until
their cost is resolved or a new month begins; migration invents no free usage.
Reconciliation is idempotent and does not depend on request ownership or success.

The application stops new admissions at its accounted/reserved USD ceiling. This
does **not** configure the OpenAI billing account or promise a provider invoice
maximum: prices/tier changes, unknown consumption and usage above reservations
must be monitored. The budget is based on configured prices, with unknown values
kept explicit. Input is bounded by `InboxAnalysis:OpenAI:MaxInputUtf8Bytes`
(default 64000, including serialized input/prompt/schema), and output by
`MaxOutputTokens`. Startup validates that the token reservation covers these
bounds plus protocol overhead before enabling the real provider. SDK retries
remain disabled; concurrency and deadlines remain bounded by worker settings.

## Usage and rollout

The real Learning adapter captures usage/model metadata before validating content,
so malformed/refused results are still accounted. Its detached invocation scope
passes only that metadata to independent reconciliation. Late/stale results can
reconcile cost without changing Domain state. Ignored cancellation retains both
the physical slot and the financial reservation until physical completion.

The production DI adapter requires a committed admission. The old synchronous
provider route cannot bypass metering: it returns unavailable until durable ingress
is configured. Schema-first deployment and the test-bot/single-ingress/single-deliverer
cutover gates in [request processing](request-processing.md) therefore remain
mandatory before using paid Learning commands. Command grammar and Domain guards
are retained. No paid provider or production deployment was used for validation.

Apply `AddSharedLearningLedger` before compatible consumers and enable source-aware
admission on all consumers together. Keep accepted work, attempts and reservations
through rollback. The Down migration refuses to remove Learning financial history
or outstanding financial reservations. Old binaries do not understand nullable
analysis sources; drain and roll back only to a compatible reader.

Inbox `/stats` keeps its Inbox-only meaning by filtering financial rows to the
Inbox source. Shared admission includes both sources. Financial rows retain
prompt/schema/output descriptors and price snapshots; logs contain technical
identifiers and usage counts without user input or response content.

The configured `gpt-6-luna` Standard short-context rates were verified on
2026-11-12: input USD 0.10, cached input USD 0.01, output USD 0.50 per million
tokens. [Official model pricing](https://developers.openai.com/api/docs/models/gpt-6-luna).
Review these explicit prices when changing model, tier, region or pricing;
unrecognized snapshots/aliases are not guessed.
