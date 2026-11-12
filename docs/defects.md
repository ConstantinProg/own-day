# Confirmed defects

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
