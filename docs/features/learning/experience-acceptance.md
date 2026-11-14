# Learning experience acceptance

The deterministic E1 tests run the complete and skipped diagnostic paths through
the webhook, PostgreSQL, actual hosted learning worker and actual hosted outbox
worker. Outbound Telegram delivery and provider operations are recording fakes.
Host stop/start, source redelivery, in-flight provider cancellation and saved help
are checked without relying on a manually invoked processor.

Natural controls include “помощь”, “что дальше”, “пауза”, “Мне пора, продолжим
позже” and “Продолжим”. Navigation help shows the saved material, latest actual
explanation and unchanged question; it does not assess or generate a new Lesson.
Legacy commands remain available. Independent mastery is not claimed by recap.

For ordinary tests configure `OWNDAY_TEST_POSTGRES_CONNECTION` to an isolated
PostgreSQL 18 database with `Pooling=false` and run the integration project. The
historical untracked request-fixture exclusion is documented in the execution
logs and defects list. Subsequent bounded-practice verification runs the entire
solution without that exclusion; its adjacent migration pair currently passes.

## Explicit paid smoke

Set these only in the test process runtime environment, never commit credentials:

- `OWNDAY_RUN_LEARNING_SMOKE=1`
- `OPENAI_API_KEY`: non-production OpenAI test-account key.
- `OWNDAY_TEST_POSTGRES_CONNECTION`: isolated test database.
- `OWNDAY_LEARNING_SMOKE_CASE_CAP_USD`: invariant decimal, USD 0.01–0.50 per case.
- `OWNDAY_LEARNING_SMOKE_INPUT_RATE`, `OWNDAY_LEARNING_SMOKE_CACHED_RATE`,
  `OWNDAY_LEARNING_SMOKE_OUTPUT_RATE`: explicitly verified model price snapshot in
  USD per million tokens, including cached input rate.

Run `dotnet test tests/OwnDay.IntegrationTests/OwnDay.IntegrationTests.csproj
--configuration Release --filter Category=LearningLiveSmoke` after building.
There are two independently isolated cases, so the suite admission cap is twice
the supplied per-case cap, at most USD 1.00. A cap below the conservative provider
reservation will reject admission before the network; choose a sufficient bounded
cap using the current price snapshot. This ledger limit is not an account-level
invoice guarantee and does not account for unrelated API traffic.

The smoke uses the current configured model and the supplied price for
`gpt-6-luna`; keep model and price snapshot consistent. Each case admits at most
four calls, one physical attempt per provider step, 30-second provider timeout and
bounded request polling. It fails on missing usage/cost, an uncompleted journey,
lost question/help or budget rejection. It never sends a real Telegram message.
The resulting test schemas are cleaned up, so export approved anonymized ledger
metadata before using this harness as a retained evaluation record; ordinary CI
does not persist pedagogical claims or a quality report.

## Deferred manual and pilot gates

After implementation, record Windows/Telegram Desktop/NVDA versions and pass/fail
for complete/skip journeys, reading order, English pronunciation, text alternatives,
focus, explanation/question return, pause/restart/resume and explicit note capture.
Use no production data. Missing manual/live results remain visible release gates.
False Answer/Clarify rates and pedagogical quality need held-out/expert evaluation;
passing fake tests or this small live smoke does not establish effectiveness or CEFR.
