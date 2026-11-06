# OwnDay documentation

Code and project files establish what is implemented. Feature guides describe current mechanisms and identify
historical contracts where compatibility matters. Product and design documents describe intended behavior;
they do not establish that every capability already exists.

## Implemented features

- [Telegram UX and command reference](features/telegram/ux.md): navigation, language, dialogs and compatibility aliases.
- [Inbox analysis](features/inbox/analysis.md): proposal contract, validation and the manual operator command.
- [Background processing](features/inbox/background-processing.md): durable jobs, recovery and worker configuration.
- [Model configuration](features/inbox/model-configuration.md): provider settings, rollout and the opt-in API smoke test.
- [Classification and canonicalization](features/inbox/classification-and-canonicalization.md): prompts, datasets and evaluation commands.
- [Automatic processing](features/inbox/automatic-processing.md): the active v2 policy and atomic application of results.
- [Telegram feedback](features/inbox/telegram-feedback.md): durable result notifications and confirmation compatibility.
- [Undo and correction](features/inbox/undo.md): whole-result undo and conflict rules.
- [Observability and cost control](features/inbox/observability-and-cost-control.md): statistics, usage and admission budgets.
- [Historical v1 policy](features/inbox/policy-v1.md): explicit evaluation and compatibility rules.
- [Learning commands](phase-5/learning-commands.md): programs, lessons, knowledge, reviews, scheduling and progress.
- [Learning acceptance and operational review](phase-5/acceptance.md): scenario coverage, configuration, migrations and limitations.

Earlier guides retain historical implementation context. Follow their links to automatic processing and the
Telegram UX guide for behavior that supersedes the original manual flows.

## Product direction and design

- [Product vision](product/OwnDay-Product-Vision.md)
- [Product requirements](product/OwnDay-Product-Requirements.md)
- [Proposed MVP architecture](design/OwnDay-MVP-Architecture.md)
- [MVP domain design](design/OwnDay-MVP-Domain-Model.md)
- [MVP user-flow design](design/OwnDay-MVP-User-Flows.md)
- [Learning platform requirements](phase-5/phase-5-requirements.md) and [implementation plan](phase-5/plan.md)

## Deployment

- [GitHub deployment configuration and runtime settings](deployment/GITHUB_SECRETS.md)
- [Production Compose configuration](../deploy/compose.yaml)
- [Local Compose configuration](../docker-compose.yml)

## Evaluation evidence and tooling

- [Measured results from 2026-10-25](../evaluation/inbox/reports/2026-10-25.md) and
  [machine-readable evidence](../evaluation/inbox/reports/2026-10-25.json)
- [Current Golden Dataset](../evaluation/inbox/golden-v2.json) and [historical dataset](../evaluation/inbox/golden-v1.json)
- [Historical dataset and report scripts](../tools/OwnDay.InboxEvaluation/scripts/README.md)

Dataset and prompt versions are retained for deterministic tests and reproducible comparisons. Raw evaluation
runs and their local archives are ignored by Git; see the script instructions for restoring archived runs.
