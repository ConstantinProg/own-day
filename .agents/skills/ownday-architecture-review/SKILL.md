---
name: ownday-architecture-review
description: Perform a principal-level architecture and code review of OwnDay changes, identifying concrete boundary, correctness, and test risks without editing code by default.
---

# Review OwnDay architecture

Review the requested diff against the current code, nearby repository patterns, root `AGENTS.md`, and only relevant `docs/` sections. Treat proposed architecture as guidance for the affected capability, not evidence that a mechanism is implemented or required for every change. By default, do not edit code when the user asks only for review.

Check, where relevant:

- Production dependency direction and Domain, Application, Infrastructure, and Host responsibilities; authority over domain transitions and whether LLM output remains a proposal.
- Telegram types crossing into Domain, transport or persistence details leaking inward, and business rules placed in controllers or routers.
- User ownership isolation, cancellation propagation, transaction boundaries, and idempotency, concurrency, and retry behavior when the changed path needs them.
- Duplicate or speculative abstractions, unnecessary wrappers or services, scope creep, alignment with existing patterns, and whether tests cover the meaningful behavior and rejection paths.

Do not require a transactional outbox, scheduler, or more elaborate concurrency machinery solely because a future architecture document describes it. Require a mechanism when the current capability or the specific change needs it.

Report actionable findings first, grouped by **Critical**, **Important**, and **Minor** severity. Omit empty categories and do not manufacture findings. For each finding give the location, concrete problem, consequence, and smallest reasonable fix. Then add **Architecture assessment** and **Validation gaps**, including when no findings were found. Distinguish observed defects from unverified risks.
