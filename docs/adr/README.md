# Architecture decision records

ADRs capture the context, decision and consequences of meaningful architecture choices. Number records sequentially using four digits and a descriptive filename. Preserve accepted records; if a decision changes, add a new record and link the superseded one.

Statuses:

- **Proposed:** under consideration.
- **Accepted:** adopted for the stated scope.
- **Superseded:** replaced by a linked later decision.

Future meaningful choices should receive ADRs when they are evaluated and selected. Do not create speculative records for unselected technologies.

| Record | Status |
| --- | --- |
| [0001: Use .NET 10 and ASP.NET Core](0001-use-dotnet-10.md) | Accepted |
| [0002: Start with a modular application boundary](0002-start-with-a-modular-application-boundary.md) | Accepted |
| [0003: Use process memory for the Phase 1 local workflow](0003-use-process-memory-for-the-phase-1-local-workflow.md) | Superseded by 0004 |
| [0004: Use SQLite for the durable local workflow](0004-use-sqlite-for-the-durable-local-workflow.md) | Accepted — implemented |
| [0005: Add status history with an explicit store upgrade](0005-add-status-history-with-an-explicit-store-upgrade.md) | Accepted — implemented |
| [0006: Bind creation keys in the local case store](0006-bind-creation-keys-in-the-local-case-store.md) | Accepted — implemented |
| [0007: Adopt EF Core Code First](0007-adopt-ef-core-code-first.md) | Accepted — implemented |
| [0008: Serve a local browser case workspace](0008-serve-a-local-browser-case-workspace.md) | Accepted — implemented |
