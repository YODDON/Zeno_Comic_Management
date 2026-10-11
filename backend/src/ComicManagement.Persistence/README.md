# Persistence operations

`ComicManagement.Persistence` owns the single `AppDbContext`, PostgreSQL migration stream,
and the infrastructure outbox/inbox tables.

## Configuration

Set the database connection through `ConnectionStrings__Database`. Do not commit credentials.
The API also accepts these optional outbox settings:

- `Outbox__BatchSize` (default `20`, range `1..100`)
- `Outbox__PollInterval` (default `00:00:01`)
- `Outbox__LockDuration` (default `00:01:00`)
- `Outbox__BaseRetryDelay` (default `00:00:05`)
- `Outbox__MaxRetryDelay` (default `00:05:00`)
- `Outbox__MaxAttempts` (default `10`)

Run migrations once as a controlled step before the API becomes ready. Do not let each API
replica migrate independently.

## Delivery guarantees

Business writes enqueue through `IOutboxWriter` and commit with the caller's `AppDbContext`
transaction. The worker claims bounded batches with `FOR UPDATE SKIP LOCKED`. An expired lease
is eligible for retry after a worker crash. Each handler is deduplicated by the
`processed_messages` primary key, written in the same PostgreSQL transaction as its database
effects and the outbox completion.

Handlers must not assume exactly-once external side effects. Provider calls require their own
idempotency and reconciliation contract. Exhausted messages remain in `Failed` state and are
logged without payloads or exception messages; no retention or deletion policy is implemented
until an operational policy is approved.
