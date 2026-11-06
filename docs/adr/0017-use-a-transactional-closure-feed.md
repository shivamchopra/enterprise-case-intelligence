# 0017 Use a transactional closure feed

- Status: Proposed
- Date: 2026-11-05

## Context

A synthetic records intake process needs to pick up cases that close while intake is stopped. Format 5 records closure in per-case history. The existing filtered case list orders by case ID, and a case that closes below a saved ID boundary is missed when the consumer continues from that boundary. The running API reproduced this with three disposable cases. [Phase 10 contract and baseline evidence](../requirements/phase-10-case-closure-handoff.md#evidence-from-the-existing-api)

## Proposed decision

Append a closure notice in the existing case-status transaction. A small retained table carries the generated position and a unique reference to the case's closure history entry. A single persisted stream identity binds checkpoints to the initialized source. The read projects the timestamp from that history entry and the fixed versioned event type. Use the same writer transaction, scoped EF context and commit-before-success boundary as the status write.

Use an ordered, bounded HTTP pull through the existing loopback `LocalDemo` mode. The consumer runs as a separate one-page command with its own receipt file. It commits receipts and its checkpoint in one local transaction. Repeating a delivered page is safe under that transaction and event identity. The following shows the two commit boundaries.

```mermaid
flowchart LR
    Caseworker[Caseworker closes a case] --> Source[Producer transaction]
    Source --> Feed[Committed closure feed]
    Feed --> Pull[Operator invokes one-page pull]
    Pull --> Sink[Receipt and checkpoint transaction]
```

Inspect the scaffolded format-6 migration and real provider behavior before implementing the feed. The offline upgrade should preserve formats 1–5 and assign a new stream identity with zero notices. Existing Closed history stays in the case file. A subsequent actual closure appends a notice through the same status path. Exact historical schema and metadata validation, rollback on interruption, and no-overwrite initialization remain gates. [EF adoption and format-5 precedent](0016-expose-case-priority-without-changing-creation-replay.md)

The first consumer needs its own explicit initialization and checkpoint inspection because a process can exit after committing and before printing success. Output counts and a position are enough to reconcile a synthetic run. A separate executable is justified for this consumer because stopping it must leave the producer available, and the exercise needs independent restart and receipt-file failure evidence.

## Alternatives and consequences

Re-scanning the closed-case list from the beginning and deduplicating every pass can eventually find the missed case in the experiment. It repeatedly visits old cases and has no position that represents a committed closure. Timestamp polling also needs tie-breaking and a policy for clock regression. A durable generated position fits the one-writer local store and the demonstrated continuation gap.

Publishing to a broker from the status request introduces a second commit boundary and requires proof of recovery when either side fails. The proposed pull lets the local producer and consumer demonstrate those recovery semantics with existing .NET and SQLite tooling. Hosted delivery, broker selection, service credentials, retention and backup restoration can be assessed from that evidence under the existing hosting boundary. [Zero recurring hosting cost decision](0012-gate-cloud-hosting-on-zero-recurring-cost.md)
