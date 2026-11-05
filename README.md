# OwnDay

OwnDay is an AI-powered execution system designed to close the gap between intention and action.

Users define their goals, commit to a daily plan, and OwnDay helps them execute through accountability, verification, adaptive guidance, and behavioral feedback.

> You choose the path.
> OwnDay makes sure you keep walking it.

See the [documentation index](docs/README.md) for implemented features, product direction, design proposals,
deployment instructions and evaluation reports. The current Telegram command reference is in
[Telegram UX](docs/features/telegram/ux.md).

Inbox capture durably schedules AI analysis in PostgreSQL; a background worker processes it when OpenAI is enabled.
Manual analysis remains available through the operator command. See
[durable background processing](docs/features/inbox/background-processing.md) for configuration,
migrations, recovery guarantees and operational limits, and
[the analysis foundation](docs/features/inbox/analysis.md) for the proposal contract and manual command.
Inbox analysis defaults to `gpt-6-luna` with low reasoning; see
[model configuration](docs/features/inbox/model-configuration.md) for VPS migration and an opt-in real API smoke test.
Classification and canonicalization use a versioned prompt and a 115-scenario Russian Golden Dataset; see
[semantic evaluation](docs/features/inbox/classification-and-canonicalization.md) for the explicit real-model
runner, review method, comparison artifacts and measured results.

## Inbox policy

The active `inbox-policy-v2` automatically applies all five supported types, including atomic composite entries,
after background analysis. Original text, proposals and application identities remain available for later correction.
See [Automatic processing](docs/features/inbox/automatic-processing.md) for mapping rules, migration,
recovery and examples. The historical [v1 policy](docs/features/inbox/policy-v1.md) remains available
for explicit evaluation and compatibility.

Telegram captures receive one durable result notification for the entire batch. Failed analysis and blocked
results retain the entry and offer manual processing; `/inbox_result <id>` opens the saved result and original.
See [Telegram feedback and confirmation](docs/features/inbox/telegram-feedback.md) for the migration,
confirmation buttons, delivery guarantees and example dialogs.

Automatic results can be undone with the **Undo entire result** button or `/inbox_undo <id>`.
Undo removes the complete unchanged batch and restores the original for manual correction with `/inbox <id>`.
Changed, completed or subsequently linked results are preserved with an explanation. See
[Undo and correction](docs/features/inbox/undo.md) for safety rules, migration and Telegram dialogs.

Use `/stats`, `/stats today`, `/stats 30d`, or `/stats all` for localized Inbox, automation, Undo, timing,
token usage and estimated OpenAI cost reports. A persistent token admission budget controls new OpenAI requests.
See [observability and cost control](docs/features/inbox/observability-and-cost-control.md) for the migration,
UTC periods, metric definitions, explicit model prices, budget limitations and examples for all four reports.
