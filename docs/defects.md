# Confirmed defects

## Local untracked legacy-migration fixture uses the current EF model

Severity: P2 (test fixture only). Observed during English diagnostic verification
on 2026-11-14. The pre-existing, untracked file
`tests/OwnDay.IntegrationTests/Telegram/PostgresLearningRequestTests.cs` is outside
the committed test suite and was preserved unchanged.

Reproduce with a PostgreSQL test connection and filter
`FullyQualifiedName~Migrate_PopulatedLegacySchema_PreservesRowsWithoutInventingRequests`.
The test migrates to the prior schema and then inserts `Lesson.Start` through the
current EF model. PostgreSQL rejects the insert with SQLSTATE 42703, missing
`Assistance` (the old schema also has no `Content`). This fails in fixture setup,
before testing preservation. Seed historical columns with SQL, then upgrade before
using current tracked entities. Equivalent preservation checks are maintained in
the tracked receipt/English diagnostic migration tests. The four cases from this
untracked class are explicitly excluded from repository regression; the complete
working-tree test suite is not claimed green.

## Learning queue health queries an unsupported provider — fixed locally

Severity: P2. Introduced by `bb46aed`, discovered during S2 regression on 2026-11-12.
[LearningQueueHealthCheck](../src/OwnDay.Host/HealthChecks/LearningQueueHealthCheck.cs)
read `learning_requests` before checking whether its PostgreSQL-only worker could
run on the configured provider. Even a disabled SQLite lane queried the queue.

Reproduction: `Check_NonPostgresProvider_DoesNotQueryUnsupportedQueue` in
[LearningQueueHealthCheckTests](../tests/OwnDay.IntegrationTests/App/LearningQueueHealthCheckTests.cs)
creates an unsupported in-memory SQLite provider without a queue schema. Both
enabled and disabled cases failed with `SQLite Error 1: no such table:
learning_requests` before the fix. Expected: disabled lane is healthy without
queries; an enabled unsupported lane is unhealthy with a configuration diagnosis.

An earlier full regression also observed one unexpected 503 from
`AppEndpointTests.HealthReady_ReturnsOk` (610 other integration tests and all 730
unit tests passed). The SQLite host shares one connection between concurrent health
checks, so the unnecessary queue query adds a connection race; that race is a
diagnostic inference, not a captured exception from the intermittent 503.

Fix: check provider capability before querying. PostgreSQL still inspects pending
work even when its worker is disabled. Verification results are recorded in the
[implementation log](product-experience-implementation-log.md).
