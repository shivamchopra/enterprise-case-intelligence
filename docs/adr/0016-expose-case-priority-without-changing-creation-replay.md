# 0016: Expose case priority without changing creation replay

- Status: Accepted — backend implemented; workspace controls pending
- Date: 2026-11-02
- Implementation amendment: 2026-11-03

## Context

The [priority requirements proposal](../requirements/phase-9-case-prioritization.md) adds mutable current priority to a shared synthetic workspace. Existing creation/list/detail/status responses have exactly four fields. Creation-key replay returns the original New result, while immutable title/description form the payload comparison. Adding mutable priority to those representations or creation input would require a separate response/key-provenance amendment.

Before this amendment the EF Code First lifecycle was deliberately constrained: format 4 validates fresh and adopted layouts, and `CaseMigrations` accepted a single initial migration. Adding a model property and running startup migration would violate the existing explicit lifecycle, rather than implement the feature safely.

## Decision

Expose current priority through separate Reader-authorized `GET /cases/{id}/priority` and CSRF-protected Caseworker `PUT /cases/{id}/priority` resources. Their representation is exactly `caseId`/`priority`. Retain all existing four-field case responses and creation inputs, original-result replay and immutable text. New creation assigns Normal atomically; a later priority edit never changes the creation payload or recorded original result.

Add an optional exact priority list filter inside the bounded EF query. Keep ID ordering and the list response unchanged. The browser loads priority only for the selected case, avoiding per-listed-case requests. Existing workload totals remain counts of current statuses across the whole workspace.

Use Domain rules and the existing concrete store/transaction boundary. Ordinary reads/writes use EF LINQ and tracked changes. Physical priority state is a required one-to-one `StoredPriority` record in `case_priorities`, with a primary/foreign case key, restrictive delete behavior, STRICT types and a Fluent API Low/Normal/High check. `StoredCase.PriorityState` is nullable at materialization so a missing dependent is detectable, rather than silently hiding its parent through an inner join. Every encountered format-5 case must have a recognized priority; reads, replay, status/priority changes and deep inspection reject missing/invalid state. This invariant spans application transactions: the foreign key prevents orphans, while creation and offline backfill ensure every case has a dependent. Direct out-of-application deletion is detected, not repaired. A filtered list validates its materialized matches/lookahead, not every excluded case; the status aggregate remains a status count, not a deep integrity probe.

The column/check candidate was actually scaffolded with EF 10.0.14 and its provider SQL inspected. It emitted `AddColumn` plus `AddCheckConstraint`, a STRICT `ef_temp_cases` rebuild/copy, then transaction-suppressed `PRAGMA foreign_keys = 0/1` around drop/rename. That cannot fit the caller-owned all-or-nothing offline transaction with foreign keys continuously enabled. Microsoft documents [SQLite check additions as rebuild operations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations#migrations-limitations). The uncommitted candidate was removed with the EF tool; only the selected related-record model and scaffolded `AddCasePriority` migration/snapshot are retained. Its single `CreateTable` generates no suppressed command, preserves every old table definition and requires no additional hand-written DDL.

Format 5 uses exactly the initial and priority migration IDs in order, with bounded expected-count-plus-sentinel metadata reads and per-migration supported EF patch validation. Expected old schemas come from the historical initial migration target model and frozen legacy shapes, rather than the latest snapshot. Captured released format-4 catalogs independently test fresh and adopted layouts. Formats 1/2/3 first receive the existing validated EF baseline; format 4 retains its original metadata row/version. The generated priority table, bounded 100-case tracked backfill of Normal, second EF metadata row, target validation and format marker share one non-deferred offline transaction. No priority event/status baseline is invented; the existing version-1 honest status-baseline rule remains.

Initialization applies the checked-in chain only to an exclusively reserved new path. Startup refuses old versions and never migrates. Offline upgrade validates source schema, every case/history/binding before mutation and target state before commit. All source values, timestamps, IDs, key ownership and original creation replay remain. Target validation includes the required priority state. Process barriers in the test executable stop after generated table creation, an actual backfill command and second metadata insertion; termination recovers a valid format-4 source. Commit-lock failure rolls back the whole upgrade, and lost completion is reconciled by inspection before retry. No transaction is automatically replayed or persistent migration lock acquired. Old executables reject the new format; there is no downgrade promise.

Priority captures current triage choice, not priority history or actor attribution. Closed cases reject an actual priority change but allow a validated same-value no-op. Existing status history remains unchanged and must not be presented as covering priority edits.

## Alternatives and consequences

- Adding priority to creation input or the four-field response would change the key/representation contract. Defer that broader API evolution; the separate resource has a concrete compatibility benefit at the cost of one selected-case read.
- Reusing a status-history entry for priority would misrepresent an unchanged status and overload its three-entry invariant. A future priority audit needs its own actor/time/retention requirements.
- Priority sorting, ownership and SLA automation need additional rules and pagination/concurrency evidence. The first slice supplies a High filter, not an automatically ordered work queue.
- A model change now has a concrete business purpose, so EF migration/adoption work is justified. It does not select SQL Server/Azure SQL, introduce another production layer or relax the $0 hosting boundary.

[The delivery task](../tasks/case-prioritization.md) defines the implementation gates. ADR 0007 remains the historical accepted EF adoption decision; this record is its format-5 compatibility amendment. Browser priority behavior remains selected but unimplemented, with acceptance tracked separately from the tested backend.
