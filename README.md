# OwnDay

OwnDay is an AI-powered execution system designed to close the gap between intention and action.

Users define their goals, commit to a daily plan, and OwnDay helps them execute through accountability, verification, adaptive guidance, and behavioral feedback.

> You choose the path.
> OwnDay makes sure you keep walking it.

Inbox capture durably schedules AI analysis in PostgreSQL; a background worker processes it when OpenAI is enabled.
Manual analysis remains available through the operator command. See
[durable background processing](docs/Phase4-Milestone2-Durable-Background-Processing.md) for configuration,
migrations, recovery guarantees and operational limits, and
[the analysis foundation](docs/Phase4-Milestone1-AI-Analysis.md) for the proposal contract and manual command.
Inbox analysis defaults to `gpt-6-luna` with low reasoning; see
[model configuration](docs/Inbox-AI-Model-Configuration.md) for VPS migration and an opt-in real API smoke test.
Classification and canonicalization use a versioned prompt and a 115-scenario Russian Golden Dataset; see
[semantic evaluation](docs/Phase4-Milestone3-Classification-and-Canonicalization.md) for the explicit real-model
runner, review method, comparison artifacts and measured results.

## Inbox policy

The active `inbox-policy-v2` automatically applies all five supported types, including atomic composite entries,
after background analysis. Original text, proposals and application identities remain available for later correction.
See [Automatic processing](docs/Phase4-Milestone5-Automatic-Processing.md) for mapping rules, migration,
recovery and examples. The historical [v1 policy](docs/Phase4-Milestone4-Deterministic-Policy.md) remains available
for explicit evaluation and compatibility.

Telegram captures receive one durable result notification for the entire batch. Failed analysis and blocked
results retain the entry and offer manual processing; `/inbox_result <id>` opens the saved result and original.
See [Telegram feedback and confirmation](docs/Phase4-Milestone6-Telegram-Feedback.md) for the migration,
confirmation buttons, delivery guarantees and example dialogs.
