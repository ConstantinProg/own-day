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
