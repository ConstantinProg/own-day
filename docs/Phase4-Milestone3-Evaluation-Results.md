# Classification and canonicalization: measured results

Actual OpenAI Chat Completions calls were executed on **2026-10-25**, using `gpt-6-luna`, low reasoning,
8192 maximum output tokens and a 60-second timeout. The adapter, strict `inbox-v1` schema and Application
validator were the production implementations. API credentials were read only from local ignored `OpenAI.txt`.
No model output was applied to Domain entities.

[Versioned machine-readable evidence](../evaluation/inbox/reports/2026-10-25.json) includes descriptors, run IDs,
prompt/schema/dataset hashes, all summaries, selected scenario IDs, paired comparisons, and original source/output
for every failed diagnostic. Full manifests, incremental raw responses and review packets remain in the ignored
`evaluation-results/live-*` directories. [Runner and review instructions](Phase4-Milestone3-Classification-and-Canonicalization.md)
describe reproduction and how to review semantics. No independent human review was performed; the generated
manual-review template remains pending for all 115 final cases. Qualitative observations below are Codex review
notes, not an objective semantic score or the result of an LLM-as-judge API call.

## Experiments

| Run | Prompt | Dataset | Cases | Structural | Joint diagnostics |
| --- | --- | --- | --- | --- | --- |
| `921bb089339a460b9745cc81c4627c60` | v1 | golden-v1 | 20 selected boundary cases | 20/20 | 16/20 |
| `d15961f6918e46f4aca8bd6d208c0108` | v2 | golden-v1 | 115 | 114/115 | 103/115 |
| `e1bd386f83d44c059177584b0d367cc7` | v3 | golden-v2 | 115 | 114/115 | 109/115 |

The first draft v2 added semantic definitions and boundaries to the original short prompt. The measured v2 run
revealed missing `unresolvedDateText`, a noncontiguous concatenation of date excerpts, and an extra Action repeating
the entire Project. v3 added focused instructions/examples for those failures. Three dataset scenarios changed
between golden-v1 and golden-v2: two synonym guards and one explicitly uncertain split-boundary example.

On the **20 identical baseline cases**, v1 → v3 joint diagnostics improved **16/20 → 19/20**, with three improvements
and no regressions. Improvements were narrower `source` extraction (`waiting-01`), unresolved future date metadata
(`date-05`) and a counterfactual classified as Reference instead of a personal deferred intention (`edge-02`).

On **112 identical v2/v3 cases**, joint diagnostics improved **103/112 → 107/112**, with seven improvements and three
regressions. Improved cases: `action-11`, `project-08`, `waiting-06`, `date-05`, `date-07`, `date-08`, `edge-16`.
Regressed cases: `reference-05`, `date-06`, `date-11`. These small development-set samples and single trials do not
establish statistical significance or production accuracy. Prompt size/input usage increased; the gain should
be checked against repeated trials, cost and a held-out dataset before further expansion.

## Final automatic diagnostics

| Diagnostic | Passed | Rate |
| --- | --- | --- |
| Structural validator | 114/115 | 99.13% |
| Allowed type multiset | 112/115 | 97.39% |
| Allowed split count | 113/115 | 98.26% |
| Type-aligned property sets | 109/115 | 94.78% |
| Type-aligned project candidate sets | 112/115 | 97.39% |
| Required ambiguity presence | 112/115 | 97.39% |
| Lexical retention guards | 112/115 | 97.39% |
| Joint coherent interpretation | 109/115 | 94.78% |
| Semantic canonicalization accuracy | **Not measured** | Independent review pending |

All 16 project-link scenarios in the final run passed joint diagnostics. This checks IDs/candidates/uncertainty
against the authored examples; it does not independently prove every inferred relation. Kind-sensitive metrics
also fail on count/type mismatches, so a failed property/link metric does not necessarily mean a property or ID
was invented. The `byTag` reports and source/output packets expose the underlying cases.

Final usage: **210851 input**, **191634 cached input**, **14543 output** tokens, including any reasoning usage.
Using explicitly supplied base rates 0.10/0.01/0.50 USD per million input/cached/output tokens, the base-token
estimate was **$0.01110954**. These rates were checked against the
[official model page](https://developers.openai.com/api/docs/models/gpt-6-luna) on the experiment date. This estimate
excludes cache-write premiums, regional/service-tier surcharges and taxes; it is not the provider's billing total.
Rates were not supplied for the earlier runs, so their saved costs remain null. Usage was available for every
successful provider response in all three live runs, including the two structurally rejected responses.

## Remaining failures and interpretation limits

- `waiting-09`: the model copied `восстановят электричество` into `source`. The rubric expects null because no
  external responsible source is named. Distinguishing the expected event from its source still needs attention.
- `reference-05`: the model preserved the negation in `Не планирую переезжать в этом году` and extracted
  `unresolvedDateText="в этом году"`, but omitted the mandatory ambiguity explanation. Application correctly rejected
  the response. This is a structural failure; its proposals will not be saved as validated results.
- `date-06`: the model returned `с 2026-10-20T09:00:00` instead of the rubric's `2026-10-20T09:00:00`. Both are exact
  contiguous excerpts, and neither invents a timezone/instant. This is a restrictive property-excerpt expectation,
  not evidence of semantic corruption. A future version can explicitly allow the prefix after review.
- `date-11`: the model left `waitingSince` null correctly, but stored the nearby letter date as unresolved date
  metadata. The rubric expects null for metadata whose semantic role is already a known historical letter date.
  No false waiting-start instant was created. Human review should decide whether this metadata is useful or noisy.
- `edge-10`: `Сохранить заметку: ...` produced Action with the quoted adversarial text retained as data, while
  the rubric expects Reference. Role markers/instructions were not executed. The Action/Reference boundary for
  an explicit request to save information needs clarification; do not report this as successful prompt injection.
- `edge-15`: measurement information appeared both in the Action description and an additional Reference.
  The rubric expects one Action. Canonical meaning/units were retained, but supporting information was oversplit.

These cases are deliberately still reported as failures; the dataset was not relaxed to conceal them. Other
known limitations include exact excerpts for inflected source names, missing long-text stress cases, potentially
ambiguous valid interpretations not exhaustively listed, and stochastic outputs. The dataset currently contains
authored synthetic examples rather than production-distribution evidence. Do not use the joint percentage as a
semantic-quality guarantee. Review every case for negation, modality, actor, conditions and missing detail.

Initial non-live attempts (`baseline-v1`, `candidate-v2`) failed at local sandbox TLS before returning provider
responses. One outside-sandbox connectivity check succeeded, followed by the three live experiments above.
Transport failures are preserved locally and excluded from model-quality comparisons. The final runner now stops
after three consecutive network failures and returns a nonzero status for provider/structural failure runs.

## Validation and next work

Final validation uses ordinary key-free tests plus the existing local PostgreSQL test database with isolated GUID
schemas. The opt-in OpenAI smoke test remains skipped in the normal suite. The explicit evaluation above is the
only new real-model execution path; standard CI does not call it.

Before a later policy/application milestone: obtain independent human judgments across all five dimensions,
define acceptable error rates/severity, reserve held-out scenarios, run repeated trials and clarify the six
remaining cases. Revalidate proposal ownership/current Inbox/project state and unsupported descriptions before
using stable proposal IDs to call existing domain use cases. Confirmation/policy/target creation remains separate
future work; none is implicitly authorized by a valid AI response or an automatic diagnostic pass.
