---
name: ownday-implement-change
description: Implement a feature, bug fix, or behavior change in the OwnDay repository, from code investigation through focused tests and final verification.
---

# Implement an OwnDay change

1. Read the root `AGENTS.md`. Inspect the current implementation, adjacent patterns, and existing tests for the requested behavior. Read only the relevant product, domain, or architecture sections in `docs/`. Code and project files establish what exists today; proposed architecture does not imply its infrastructure is already present or must be built now.
2. Identify the actual boundaries affected by the request. Preserve domain authority and the repository dependency direction. Put invariants and meaningful state transitions in Domain, use-case coordination in Application, external adapters in Infrastructure, and composition and transport concerns in Host. Change only the layers the behavior needs; do not mechanically touch every layer.
3. Search for an equivalent abstraction before adding a service, DTO, wrapper, or interface. Prefer the smallest change consistent with current repository patterns. Avoid speculative abstractions, roadmap implementation, unrelated refactoring, and scope creep.
4. Treat LLM output as an interpretation or proposal, never authoritative domain state. Keep Telegram-specific types out of Domain, and business rules out of controllers and transport routers. When the change involves persistence or delivery, check the required ownership and transaction boundaries against the current capability rather than assuming future mechanisms exist.
5. Add or update tests at the level that can verify the changed behavior, including relevant failure, rejection, and cancellation paths. For a genuinely multi-layer change, implement from Domain through Application and Infrastructure to Host where practical; this order is not required when only some layers change.
6. After implementation, use `ownday-verify-change` if available. Finish with a compact report of the change, tests and checks actually run, and any material limitation.
