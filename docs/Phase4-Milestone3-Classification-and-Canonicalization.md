# Phase 4 — Milestone 3: Classification and Canonicalization

Inbox analysis still produces proposals, using the M1 Application validator and the M2 durable processing path.
No entity creation, Policy Engine or Telegram confirmation is introduced. The current contract/schema remains
`inbox-v1`; the production prompt is `inbox-analysis-v3`. Domain entities, migrations, background job lifecycle,
outbox and Host composition are unchanged.

## Interpretation rules

The versioned prompts live in `src/OwnDay.Infrastructure/OpenAIAnalysis/Prompts/` as embedded resources.
`OpenAIInboxAnalysisFormat` selects an explicit version; unknown versions fail instead of falling back.
The production adapter defaults to v3. The evaluation tool can select the preserved v1/v2/v3 resources.

- Action means a concrete current action, including investigating whether a change is feasible. Investigating
  Redis is different from committing to migrate to Redis; asking for an answer is different from already waiting.
- Project means a committed independent outcome requiring several actions. Components of an outcome are not
  automatically separate Actions. Explicit next actions can accompany the Project without new proposal-to-proposal links.
- WaitingFor means an actual pending external result. Received results, future conditional waits and reminders
  to send a request are distinguished. `source` identifies the stated external source, with exact source-text evidence.
- Reference retains facts, quotations, reflections, refusals and negations without creating an obligation.
- SomedayMaybe retains tentative, deferred or conditional personal intentions. A technological hypothesis alone
  does not establish a personal intention.

Canonical wording preserves actors, conditions, negation, modality, numbers and meaningful details, correcting
grammar and removing filler. The 200-character limits of current targets still apply. Proposal descriptions can
retain details but are not supported as separate fields on every target. Unknown properties remain null; no
deadline/status/priority fields are added. Project titles are evidence, not instructions or authority to infer facts.

Splitting distinguishes independent intentions from shopping lists, alternatives, short procedures and repeated
mentions. Ambiguous boundaries remain explicit. The response stays within 1–20 proposals. `projectIds` refer only
to the supplied existing-project snapshot; multiple IDs are alternatives, never multiple confirmed links.
Truncation is not evidence that an omitted project does not exist. Topical similarity is insufficient for a link.

`waitingSince` requires a stated start of an actual wait, exact ISO offset evidence, a UTC result and an instant no
later than capture. A deadline, letter date or nearby timestamp does not establish that role. Relative dates are
never resolved using server time or capture time. `unresolvedDateText` is analysis metadata for every kind, even
though it is not a target-domain property in `SupportedTypes`. It must be a contiguous, case-preserving excerpt,
with an ambiguity explanation; an ambiguity alone does not replace that field. Existing independent structural
validation remains the final gate before proposals are persisted. Semantic correctness still requires review.

## Golden Dataset

`evaluation/inbox/golden-v2.json` is the current dataset: **115 authored Russian scenarios**, with colloquial
language, typos and synthetic examples. It is not a sample of private production Inbox data. The initial
`golden-v1.json` is preserved for historical experiments. Each scenario has:

```text
id, tags, text, capturedAt (UTC)
context: timeZoneId, projects [{ id, title, createdAt }], projectsTruncated
variants[]: reason, items[]
  kinds[]                    allowed classifications
  propertySets[]             allowed complete property values; null requires absence
  projectIdSets[][]          allowed sets of candidate IDs; [] means no link
  requireAmbiguity           checks presence, not correctness of explanation
  contentAnchorGroups[][]    broad alternatives for lexical retention diagnostics
reviewCriteria: classification, splitting, properties, links, canonicalization
```

Variants explicitly allow plausible alternative interpretations, such as one Action retaining an address versus
an Action plus a Reference. Order is irrelevant: scoring uses bipartite matching, without reusing an output item.
The joint check requires one coherent variant/matching, so incompatible pieces of different variants cannot pass.
Exact titles are never expected. Lexical guards can produce false negatives for synonyms and cannot prove semantic
equivalence. Properties/link checks are type-aligned set checks, and splitting_count checks cardinality only.
Provider/structural failures remain in the denominator. Reports show completed/selected counts to expose partial runs.

v2 fixes two lexical omissions (`бумажный`, `электроснабжение`) and makes the conversation-boundary scenario
explicitly uncertain. It does not rewrite expectations to accept model errors. Comparisons pair only identical
scenario hashes, including their context and expectations, excluding these three changed cases.

Normal unit tests validate both datasets, coverage, evidence and project references using the production validator.
They also check semantic diagnostics on constructed proposals, matching, request bounds, cancellation, safe failure
reports, cost arithmetic, review completeness and comparison isolation. These tests never call a model.

## Explicit evaluation command

The separate .NET console project `tools/OwnDay.InboxEvaluation` references the existing adapter and validator. It
does not start Host, Telegram, PostgreSQL or workers. Ordinary `dotnet test` does not execute its entry point.
No key is required for builds or deterministic tests. From the repository root:

```powershell
dotnet build OwnDay.slnx --configuration Release

# Offline validation; no key or network needed.
dotnet run --project tools/OwnDay.InboxEvaluation --configuration Release --no-build -- validate

# Paid real calls, bounded by the explicit maximum. Key is read locally and never printed/copied.
dotnet run --project tools/OwnDay.InboxEvaluation --configuration Release --no-build -- run `
  --settings-file ./OpenAI.txt `
  --dataset evaluation/inbox/golden-v2.json `
  --model gpt-6-luna --prompt-version inbox-analysis-v3 `
  --max-requests 115 --output evaluation-results/my-v3-run `
  --revision my-reviewed-revision
```

Alternatively supply `OPENAI_API_KEY` in the process environment and omit `--settings-file`. The explicitly
selected local file must contain exactly one `sk-...` credential. Only the key is read from it; model, effort and
limits use CLI settings/defaults. No endpoint from the file is accepted. **`OpenAI.txt` and `evaluation-results/`
are ignored by Git.** Keep all real-user evaluation artifacts private, including review packets.

`--max-requests` is mandatory, 1–10000, and is a ceiling, not padding: only selected scenarios are called once.
Use `--ids action-03,date-01` or `--tag injection`, and `--offset N` after filtering. The tool is sequential and
disables SDK retries. An output directory must be new or empty; an existing experiment is never overwritten.
Auth/quota/rate-limit/permanent errors stop the run. Three consecutive transport/timeouts also stop it, avoiding
an uncontrolled sequence of network failures. Each call has its own deadline; Ctrl+C preserves completed results.
Exit codes: 0 completed with structurally valid responses; 1 partial/provider/invalid-response run; 2 invalid
configuration/artifacts; 130 cancellation. Semantic diagnostic failures alone do not make the command fail.

Model defaults match production: `gpt-6-luna`, `--reasoning-effort low`, `--max-output-tokens 8192`,
`--timeout-seconds 60`. Reasoning is sent only for the exact model supported by the production options. Other
models must support strict Structured Outputs and use the same adapter's compatibility policy. The tool does
not add a new provider or automatic fallback. To reproduce, retain dataset/prompt versions, source revision,
effective descriptor, model, selection and SDK/runtime versions, then repeat the command with a new output folder.
Provider alias updates and stochastic generation prevent bit-for-bit reproducibility; run repeated trials before
drawing strong conclusions. The manifest captures requested model, while each response retains returned model.

## Artifacts, review and comparison

Every experiment saves:

| File | Contents |
| --- | --- |
| `run.json` | Run ID, UTC start, descriptor/scorer version, complete selected scenarios, dataset hash, exact prompt/schema plus hashes, revision label, SDK/runtime and optional price rates |
| `results.jsonl` | Incrementally flushed result per completed call: response JSON/model/ID, usage, duration, safe status, automatic checks and optional cost |
| `summary.json` | Automatic metrics overall/by tag, failure IDs, completion counts, token totals and known/unknown cost coverage |
| `summary.md` | Readable diagnostic table and explanation of review limits |
| `review-packet.json` | Each source/context, acceptable variants/rubric, output and checks together, including adversarial strings as data |
| `manual-review.json` | Per-dimension pending/pass/fail/uncertain decisions, notes, reviewer identity and scenario hashes |
| `manual-summary.json` | Created by `review`; reviewed counts for all five dimensions, including pending/uncertain |

Hashes use SHA-256 of the tool's canonical JSON serialization, not raw source-file bytes. The complete snapshot
remains available even after a dataset edit. Hard process termination can leave the manifest and flushed lines
without final summary; `render` can recreate the readable report/review packet from intact completed lines.
`render` does not overwrite review decisions. A truncated final JSONL line must be diagnosed/preserved separately
before rendering; the reader does not silently discard a malformed result.

For semantic review, inspect **every** source/output against all five scenario criteria. Mark `pass` only when
meaning, modality, negations and significant details are preserved; use `uncertain` for defensible alternative
readings not covered by the current rubric. Record notes for failures/uncertainty and identify the reviewer,
including whether the review was human or AI-assisted. Do not count pending/uncertain as passes. A second human
can review a copy and reconcile disagreements. No LLM judge is called; any AI-assisted review is a judgment,
not objective ground truth. Canonicalization accuracy is not inferred from lexical guards.

```powershell
# After editing manual-review.json; no API call.
dotnet run --project tools/OwnDay.InboxEvaluation -c Release --no-build -- review --run evaluation-results/my-v3-run

# Identical inputs/context/expectations are paired; unmatched scenarios are reported separately.
dotnet run --project tools/OwnDay.InboxEvaluation -c Release --no-build -- compare `
  --baseline evaluation-results/my-v1-run --candidate evaluation-results/my-v3-run `
  --output evaluation-results/v1-vs-v3.json

# Recreate readable artifacts from a historical run without changing its judgments.
dotnet run --project tools/OwnDay.InboxEvaluation -c Release --no-build -- render --run evaluation-results/my-v3-run
```

Comparisons preserve each paired case's before/after checks, regressions/improvements, token deltas and both
descriptors/dataset hashes. Different scorer versions are rejected. Compare manual reviews and raw outputs alongside automatic results. Different
selection sizes are not directly comparable as overall percentages; use the identical-case pairs.

Input, output (including reasoning) and cached-input usage come from the SDK response. Cost stays **null** unless
all three base USD-per-million rates and usage fields are available. Optional flags are
`--input-usd-per-million`, `--cached-input-usd-per-million`, `--output-usd-per-million`. Estimated base-token cost:
`((input - cached) * inputRate + cached * cachedRate + output * outputRate) / 1_000_000`.
It is not a billing statement: cache-write premiums, regional/service-tier surcharges and taxes are not represented
by the SDK fields used here. Failed calls may incur unreported cost; missing usage is never treated as free.

## Compatibility and deployment

No analysis JSON/schema change or database migration is required. Completed `inbox-v1` history remains readable.
The optional cached token field is response telemetry, not a new domain property or persisted proposal field.

M2 compares the entire saved descriptor before retry. A Pending analysis started with v1/v2 and resumed with the
v3 adapter terminates safely as `descriptor_mismatch` without a new provider call, preserving metadata. New queued
jobs that have not yet started use v3; already-finished history is acknowledged normally. Request a new manual
analysis version for failed stale attempts using the existing operator command; do not relabel old analysis rows.
Keep instances on a consistent release. Existing tests cover these paths, with new explicit historical-prompt tests.

## Observed experiment and limitations

See [the measured run report](Phase4-Milestone3-Evaluation-Results.md) for actual calls, metrics, known errors,
review status and preserved evidence. These are development-set experiments, not an unbiased production benchmark.
The dataset informed the prompt; add held-out examples and repeated trials before setting a release quality gate.

Next work should define a reviewed semantic-quality threshold, complete independent human review, add real
anonymized/consented hold-out examples, and resolve source/date/splitting edge cases with measured prompt changes.
For a later application/confirmation milestone, consume stable proposal IDs, revalidate current Inbox/project
ownership/state and domain limits, preserve explicit user authority, and define how descriptions/ambiguities map
to targets that do not have those fields. No automatic application policy is implemented by this milestone.
