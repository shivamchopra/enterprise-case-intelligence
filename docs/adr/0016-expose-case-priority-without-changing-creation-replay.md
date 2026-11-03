# 0016: Expose case priority without changing creation replay

- Status: Proposed — contract/design only; no runtime or schema implementation
- Date: 2026-11-02

## Context

The [priority requirements proposal](../requirements/phase-9-case-prioritization.md) adds mutable current priority to a shared synthetic workspace. Existing creation/list/detail/status responses have exactly four fields. Creation-key replay returns the original New result, while immutable title/description form the payload comparison. Adding mutable priority to those representations or creation input would require a separate response/key-provenance amendment.

The current EF Code First lifecycle is also deliberately constrained: format 4 validates fresh and adopted layouts, and `CaseMigrations` accepts a single initial migration. Adding a model property and running startup migration would violate the existing explicit lifecycle, rather than implement the feature safely.

## Proposed decision

Expose current priority through separate Reader-authorized `GET /cases/{id}/priority` and CSRF-protected Caseworker `PUT /cases/{id}/priority` resources. Their representation is exactly `caseId`/`priority`. Retain all existing four-field case responses and creation inputs, original-result replay and immutable text. New creation assigns Normal atomically; a later priority edit never changes the creation payload or recorded original result.

Add an optional exact priority list filter inside the bounded EF query. Keep ID ordering and the list response unchanged. The browser loads priority only for the selected case, avoiding per-listed-case requests. Existing workload totals remain counts of current statuses across the whole workspace.

Use Domain rules and the existing concrete store/transaction boundary. Ordinary reads/writes remain EF LINQ/tracked changes. Priority state, whether represented by a case column or a required related record, must be defined in C#/Fluent API and owned by scaffolded EF migrations. Prefer the smallest model that passes real-provider lifecycle tests; physical storage is a verification item, not a selected provider exception in this proposal.

Before implementation is accepted, prove generated operations preserve STRICT tables, constraints, retained relationships and one offline transaction, including EF metadata and format marker. A column/check amendment may require SQLite rebuild operations; inspect them rather than assuming they fit the current runner. Microsoft lists check-constraint additions among [SQLite provider rebuild operations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations#migrations-limitations); that documentation is not evidence that a generated rebuild fits this application's transaction/STRICT contract. If a required one-to-one record is the justified alternative, document its invariant, atomic creation/backfill and missing-record detection before adopting it. Neither option permits application-side whole-table materialization, a parallel hand-written upgrade script or weakening compatibility. Resolve the physical choice with actual generated migration/provider evidence in the implementation PR and amend this record accordingly.

Propose format 5 after that proof. Keep supported formats 1/2/3/4, frozen source schemas, historical migrations and original retained values. Initialization/upgrade remain explicit, offline and no-overwrite. Validate source data before mutation and target state before commit. Reconcile uncertain outcomes by inspection; never infer that missing completion output permits repeating a committed upgrade.

Priority captures current triage choice, not priority history or actor attribution. Closed cases reject an actual priority change but allow a validated same-value no-op. Existing status history remains unchanged and must not be presented as covering priority edits.

## Alternatives and consequences

- Adding priority to creation input or the four-field response would change the key/representation contract. Defer that broader API evolution; the separate resource has a concrete compatibility benefit at the cost of one selected-case read.
- Reusing a status-history entry for priority would misrepresent an unchanged status and overload its three-entry invariant. A future priority audit needs its own actor/time/retention requirements.
- Priority sorting, ownership and SLA automation need additional rules and pagination/concurrency evidence. The first slice supplies a High filter, not an automatically ordered work queue.
- A model change now has a concrete business purpose, so EF migration/adoption work is justified. It does not select SQL Server/Azure SQL, introduce another production layer or relax the $0 hosting boundary.

[The delivery task](../tasks/case-prioritization.md) defines the implementation gates. ADR 0007 remains the accepted current format-4 decision; this proposed amendment must not be read as shipped format-5 support or proof that an unscaffolded migration is safe.
