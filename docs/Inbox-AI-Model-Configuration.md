# Inbox AI model configuration

Inbox analysis uses `gpt-6-luna`, Chat Completions and strict `response_format=json_schema` with the existing
`inbox-v1` schema and prompt. `IInboxAnalyzer` keeps the same members. AI results remain independently validated
proposals and never change Domain entities automatically.

## API and SDK compatibility

Checked against official OpenAI documentation on 2026-10-25:

- [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna) supports Chat Completions,
  Structured Outputs and reasoning efforts `none`, `low`, `medium`, `high`, `xhigh`, `max`.
- [Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=chat)
  supports a strict JSON Schema response format. This adapter sends no function tools; the model's restriction
  on function calling with reasoning in Chat Completions does not apply to this request.
- [Chat Completions parameters](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)
  define `reasoning_effort` and `max_completion_tokens`; the latter bounds reasoning and visible output together.

The existing stable OpenAI .NET SDK 2.8.0 can serialize these requests and parse the responses. No SDK upgrade or
Responses API migration is necessary. Its typed reasoning property is experimental (also checked in 2.14.0), so
the adapter uses the public `ModelReaderWriter` API to initialize that documented request field. HTTP transport
tests verify the actual serialized payload, including configured reasoning values and token limits, without
warning suppression. Temperature, top_p, tools and deprecated max_tokens are not sent.

## Settings

All keys below are environment variables; equivalent JSON settings live under `InboxAnalysis:OpenAI`.

| Variable | Default | Meaning |
| --- | --- | --- |
| `InboxAnalysis__OpenAI__Enabled` | `false` | Enable provider; background processing also requires its own Enabled flag |
| `InboxAnalysis__OpenAI__ApiKey` | unset | Runtime secret; missing key pauses background processing |
| `InboxAnalysis__OpenAI__Model` | `gpt-6-luna` | Model must support Chat Completions and strict Structured Outputs |
| `InboxAnalysis__OpenAI__ReasoningEffort` | `low` | Allowed: none, low, medium, high, xhigh, max |
| `InboxAnalysis__OpenAI__MaxOutputTokens` | `8192` | Total reasoning + JSON budget, validated within 256–16384 |
| `InboxAnalysis__OpenAI__TimeoutSeconds` | `60` | Provider network timeout, 1–300 seconds |

Reasoning is sent only for the exact verified model ID `gpt-6-luna`. Other/custom model IDs receive no reasoning
parameter; their capability/access must be checked separately. To use the previous model, set
`InboxAnalysis__OpenAI__Model=gpt-4.1-mini`; the default low setting is then omitted from the request and stored
as null effective reasoning. Unsupported models fail safely without switching model or output format.

8192 is a bounded initial budget for this short extraction task, not a guarantee that every input will fit.
Reasoning consumes part of it. A length finish reason becomes `incomplete_response`, with no retry or automatic
budget increase. At default three job attempts, provider completions are bounded by 3 × 8192 output/reasoning
tokens per job; failures can still incur input/output cost. Manual invocations create separate budgets.
Background processing additionally imposes `InboxAnalysis__Background__TimeoutSeconds` (default 60); the first
timeout reached wins. Keep `LeaseSeconds` at least Background TimeoutSeconds + 30 (default 120).

## Persistence and restart behavior

Migration `20261025121850_AddInboxAnalysisParameters` adds nullable `reasoning_effort`, `max_output_tokens` and
`timeout_seconds` to `inbox_analyses`. At analysis start these capture effective request reasoning, the total
output limit and provider timeout. Returned model IDs replace the configured alias on terminal results, as before;
safe diagnostics keep the completion ID and token counts (output usage includes reasoning).

On retry/restart, the saved descriptor must match the current model/schema/prompt and all three saved parameters.
Mismatch terminates the same job/version with `descriptor_mismatch`, without a provider call or retry. It retains
the original snapshot. Legacy rows retain null parameters, because historical configuration cannot be inferred;
Pending legacy analyses therefore fail the comparison with the real adapter. Completed history stays readable.
Untouched queued jobs have no analysis snapshot yet and use current settings when first started.

Existing lease fencing, retry limits/backoff, cancellation, atomic result/proposal/job persistence and protection
against saving completed results twice remain in force. A crash can still cause a repeated billable API call;
database persistence remains idempotent. Failed jobs are terminal. Use the documented manual operator command to
create a new analysis version when required; changing settings alone does not resubmit failures.

## VPS rollout

1. Stop all old app instances before the migration. Avoid running old workers alongside the new release: old
   code cannot check the new saved parameters.
2. Deploy the new image and apply migrations using the existing migration bundle. From the deploy directory:
   `docker compose -f compose.yaml run --rm migrate`.
3. In the runtime `.env`, replace any old Model override with `InboxAnalysis__OpenAI__Model=gpt-6-luna`, and set
   `InboxAnalysis__OpenAI__ReasoningEffort=low` and `InboxAnalysis__OpenAI__MaxOutputTokens=8192`. Remove obsolete
   overrides if relying on appsettings defaults. Keep the existing API key in the secret environment.
4. Recreate the app with the new image/environment: `docker compose -f compose.yaml up -d --force-recreate app`.
   Settings are read at startup. Configure all app instances consistently. The current deployment workflow runs
   the migration bundle before recreating the app; arrange the stop step for this rollout.
5. Check safe job outcome codes, especially `descriptor_mismatch`, `configuration`, `permanent_failure`,
   `incomplete_response` and `budget_limit`. Do not enable request/response body logging.

The default Enabled flags, retry counts and lease durations need no change. API access to this model and billing
are account-dependent and must be verified with the explicit smoke test.

## Explicit real API smoke test

Standard `dotnet test` skips the `OpenAISmoke` test regardless of key availability unless explicitly opted in.
It uses synthetic text, real DI/SDK/strict schema and Application validation, requires no database, and prints
neither keys nor user/provider text. It makes one paid request with a 60-second deadline and 8192 token cap.

Supply `OPENAI_API_KEY` through a secret environment first, then in PowerShell:

```powershell
$env:OWNDAY_RUN_OPENAI_SMOKE = '1'
try {
    dotnet test tests/OwnDay.IntegrationTests --configuration Release --filter 'Category=OpenAISmoke'
}
finally {
    Remove-Item Env:OWNDAY_RUN_OPENAI_SMOKE -ErrorAction SilentlyContinue
}
```

If explicitly opted in without a key, the test fails rather than pretending to pass. Do not set the opt-in flag in
standard CI. A mocked transport test proves wire compatibility, not account access or a successful live request.
