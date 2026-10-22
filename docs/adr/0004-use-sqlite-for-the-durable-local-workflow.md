# 0004: Use SQLite for the durable local workflow

## Status

**Accepted — implemented for the durable local Phase 2 workflow.** Reviewed in PR #11 following the requirements review in PR #10. Supersedes [ADR 0003](0003-use-process-memory-for-the-phase-1-local-workflow.md) for Phase 2. The HTTP contract remains unchanged; storage now uses a retained local SQLite file.

[ADR 0005](0005-add-status-history-with-an-explicit-store-upgrade.md) is the accepted, implemented Phase 4 amendment for a history table and explicit version-1 → version-2 upgrade. This record preserves the original version-1 decision; ADR 0005 records paired history writes, and [ADR 0006](0006-bind-creation-keys-in-the-local-case-store.md) now defines creation-key bindings and the current version-3 format/upgrade commands. The SQLite choice and local operating/recovery limits remain in effect.

## Context

At decision time, the API created, retrieved and progressed synthetic cases but lost all data when its host stopped. Phase 2 requires acknowledged writes to survive application restart and abrupt API-process termination, assuming retained, intact storage. It also requires atomic status operations, deliberate store initialization, unchanged HTTP/domain behavior and safe storage failures.

The supported scope is one local API process using one store at a time, with small synthetic demonstrations and no measured throughput requirement. Shared storage between application instances, real records, cloud deployment, hardware/power-loss recovery, backup and high availability remain outside scope. A future intention to evaluate SQL Server / Azure SQL does not by itself require a server database for this local capability.

## Decision

Use a **file-backed SQLite database accessed through Microsoft.Data.Sqlite**, using parameterized SQL in the existing Api project. Keep the Domain library independent of storage. Replace the concrete in-memory store when implementing this decision; do not keep a runtime memory fallback or selectable second provider.

Add the main Microsoft.Data.Sqlite package on a stable .NET 10-compatible release, with an explicit version selected and verified during implementation. Its default native SQLite bundle avoids a separately installed database engine or CLI; verify the resolved engine version and required features on local macOS and Linux CI. Do not add EF Core, Dapper, custom native builds, a database server, containers or another production project for this slice. See the official [provider overview](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) and [native bundle documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions).

This is a project-specific judgment: SQLite's local application storage model matches the reviewed single-process need with less setup than a server. It does not establish the eventual enterprise database. SQLite's [appropriate-use guidance](https://www.sqlite.org/whentouse.html) distinguishes local storage from network/shared and high-writer-concurrency workloads.

### Configuration and initialization

Require an explicit `CaseStorage:DatabasePath`, supplied through standard .NET configuration, for both initialization and normal startup. Accept an absolute ordinary local file path; reject empty/relative values, URI filenames and in-memory/temp-store forms. Use `SqliteConnectionStringBuilder`, not connection-string concatenation. The directory must already exist and permit the database and rollback journal to be written. Document use of a local filesystem outside network shares or synchronization-managed folders; the application does not certify filesystem behavior.

Normal connections use `Mode=ReadWrite`, `Cache=Private` and `Pooling=False`. ReadWrite does not create a missing database; disabling pooling makes connection disposal and isolated test cleanup explicit. Each operation owns and disposes its connection, commands, readers and transaction. These settings follow the documented [connection options](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings).

Add an explicit `init-store` command mode to the existing Api executable, which initializes storage and exits without starting HTTP hosting. It takes the same configuration key. Reserve a new database file with exclusive create semantics; fail if any file already occupies the target path, even if empty or invalid. Only that newly reserved file may be opened in create mode. Create the initial schema and version in one transaction. On failure, exit unsuccessfully and leave any newly created artifact for deliberate inspection/disposal; never delete or overwrite an existing path. Do not run initialization concurrently with an API process using the same store.

Before normal hosting begins, open the configured existing file, validate the expected schema/version and journal mode, and run a SQLite integrity check. Check write access using a metadata write inside a transaction that is always rolled back, leaving committed state unchanged; merely opening the file or beginning a transaction is insufficient to exercise journal creation. Fail startup on missing, unreadable, incompatible, corrupt or unwritable storage. Do not run `CREATE TABLE IF NOT EXISTS`, automatic migrations, seeding, resets or repair as part of normal startup. SQLite's own rollback-journal recovery is normal engine recovery, not an application reset.

### Schema and domain restoration

Use one `STRICT` table, `cases`, with four non-null `TEXT` columns: canonical UUID `id` as primary key, `title`, `description` and exact status name `status`. Add a status check for `New`, `InReview` and `Closed`. Set `PRAGMA user_version` to `1` during initialization; reject unrecognized versions or table/constraint shapes. Future schema changes need an explicit compatibility/migration decision. [STRICT tables](https://www.sqlite.org/stricttables.html) provide type constraints; they do not replace domain validation.

Add a deliberate restoration path to `Case` that preserves a supplied non-empty ID, validated normalized text and defined status. Keep the existing new-case constructor and transition matrix. Reject invalid stored values rather than generating an ID, trimming damaged stored text, defaulting a status or replaying transitions. Test restoration directly in Domain.Tests without a provider dependency. Do not add public property setters or ORM attributes.

Read stored status names with exact matching, and validate canonical IDs when mapping rows. Continue enforcing .NET string-length and trimming rules in the domain; database text functions are not a replacement for those rules. Parameterize case values, including IDs. Do not concatenate submitted text into SQL; see the provider's [parameter guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/parameters).

### Transactions and concurrency

Register one concrete SQLite case store per host with one private synchronous gate. Its constructor holds configuration, without opening storage during request dependency resolution; preserve input validation before operation I/O. Serialize each complete operation, including reads, through that gate. Open a fresh connection inside it; do not share ADO.NET objects between requests or keep an entity cache. Return immutable `CaseSnapshot` values and keep HTTP parsing/serialization outside the gate.

- **Create:** validate a new domain case, begin a write transaction, insert without replacing an existing ID, commit, then return the captured snapshot.
- **Retrieve:** read by ID and restore/validate the complete row, returning a snapshot or absence. A provider or invalid-row failure must not be reported as absence.
- **Change status:** begin a non-deferred write transaction before lookup; restore the row, apply the domain rule, update only status when it changes, and commit before returning the snapshot. Confirm a changed update affects exactly one row. A same-status request commits a no-op; missing cases and rejected transitions perform no write.

Use `BeginTransaction(deferred: false)` so status lookup does not precede the write transaction. Do not commit outside the gate or publish a snapshot before commit. SQLite transactions supply the storage atomicity; the host gate supplies a simple operation order for this supported single-process workflow. The provider documents [transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions), including the upgrade hazards of deferred transactions. No concurrent request arrival order, stale-client token or multi-process guarantee is added.

Keep provider access synchronous: Microsoft.Data.Sqlite's [async methods execute synchronously](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async). Do not wrap calls in `Task.Run` or hold a C# lock across an await. This deliberately trades throughput for a small inspectable local implementation; it is not a measured performance claim.

### Journaling, failure and response semantics

Initialize with rollback journaling (`journal_mode=DELETE`); verify that mode when opening existing stores. Set and verify `synchronous=FULL` on each connection before starting transactions. Keep journal files with the database and let SQLite manage them; never manually remove a journal to recover a running or crashed store. Use `user_version` only as application-owned schema metadata. These settings and recovery behavior are described in the [PRAGMA reference](https://www.sqlite.org/pragma.html) and [atomic commit explanation](https://www.sqlite.org/atomiccommit.html). WAL and checkpoint management add no needed reader/writer parallelism while the host gate serializes operations, so defer them.

Success follows a completed commit. A known pre-commit failure rolls back the transaction; a commit error or lost response can have an uncertain outcome. Never retry a whole write automatically, infer rollback from a failed response, or fall back to memory. A client can reconcile a known ID by reading it; unacknowledged create retries can duplicate cases. This ADR promises only the recovery scope defined by Phase 2, not survival of storage loss or machine power failure.

Set a finite five-second provider command/default timeout. Microsoft.Data.Sqlite can retry busy/locked operations within that timeout; this is distinct from replaying a completed or uncertain transaction. Do not add an outer transaction-retry policy. Provider connection/command objects must not be used concurrently; see the official [error and timeout guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors). The synchronous gate can still queue requests; the timeout is not an end-to-end request deadline.

Preserve the existing `500 internal_error` problem contract for provider, configuration or invalid-row failures after startup. Catch the domain's rejected-transition exception only around `Case.ChangeStatus`, conveying that explicit outcome to the endpoint as `409 invalid_transition`; do not let a broad `InvalidOperationException` catch turn a storage failure into a business conflict. Existing input errors and missing cases retain their Phase 1 behavior. Do not expose provider messages, paths, SQL, case text or stack traces in HTTP problems. Startup/initialization failures use a concise diagnostic and nonzero exit, without dumping configuration or case contents.

`GET /` and `/health` retain their current behavior in a running host; health remains liveness without a storage-readiness promise. No reset, initialization or diagnostic HTTP route is added.

### Local setup and repository boundaries

The future walkthrough will document the required path, one-time `init-store`, normal startup against the retained file, restart/retrieval and deliberate disposal after the process stops. No cloud account, database-server installation, credentials or separate SQLite CLI is needed for this choice. Keep database files, journals and local configuration out of Git with explicit ignore rules added during implementation; commit schema creation code and documentation instead. This decision adds no encryption, authentication, authorization, disk quota or backup capability. Use synthetic data only.

Api continues to reference Domain. The SQLite store, initializer and schema checks stay in Api; introduce only small types needed for outcomes and configuration. Domain restoration is the only new domain capability. Avoid a generic repository, provider interface, unit-of-work framework or empty persistence project. Replacing SQLite later may require real code/schema changes; no drop-in SQL Server compatibility is claimed.

## Alternatives considered

These are judgments against the reviewed local requirements, not database performance rankings.

| Option | Fit and tradeoff | Outcome for this slice |
| --- | --- | --- |
| SQLite + Microsoft.Data.Sqlite | Embedded transactions, a retained local file and actual-engine tests without a server; synchronous I/O and limited writer concurrency are acceptable here. | Accepted. |
| SQL Server / Azure SQL | Would provide a server-managed relational boundary, but adds provisioning, connection/access configuration and a separate test service. The local workflow does not require shared server storage. | Defer until requirements justify it; no incompatibility with the long-term direction is implied. |
| PostgreSQL or another server database | Also adds server lifecycle and integration setup without a reviewed need for it. | Defer for the same scope reason. |
| SQLite + EF Core | Keeps local storage benefits but adds ORM mapping/change tracking and migration conventions for three small operations. | Prefer direct SQL now; revisit if mapping/query complexity justifies an ORM. |
| Custom JSON file | Appears small but makes atomic replacement, flushing, recovery, schema validation and interrupted writes application responsibilities. | Reject custom storage mechanics for this requirement. |
| Existing dictionary or an in-memory database/provider | Easy isolated tests, but no retained file and no application-restart recovery. | Does not satisfy Phase 2. |

## Required implementation evidence

The implementation and automated tests cover every [P2 criterion](../requirements/phase-2-durable-case-workflow.md#acceptance-criteria):

| Criteria | Required verification with this design |
| --- | --- |
| P2-01, P2-04, P2-08 | Run case HTTP contracts and lifecycle/error tests with real temporary SQLite files; reopen independently to verify committed state and case independence. |
| P2-02, P2-09 | Dispose/recreate hosts against one retained file; separately run hosts with distinct files. Include new creation after restart and all three retained statuses. |
| P2-03 | Initialize a temporary file, launch the built API as a child process, wait for readiness and acknowledged HTTP writes, force-kill that process, then start a fresh process against the file. Assert original IDs/text/statuses. Use bounded readiness polling, not fixed sleeps; clean up child processes even on failure. |
| P2-05 | In isolated fixtures, install a SQLite trigger that aborts an insert/update after the write transaction has started. Test both operations through HTTP, then reopen the file and prove rollback/no partial state. Separately force a provider availability/locking failure and check safe `500` behavior. Do not rely solely on mocked storage exceptions. |
| P2-06 | In the test host, wrap response delivery after a real successful operation to suppress the response or throw before it reaches the caller. Reopen the file and verify the commit, read the known ID and retry its current status. This controls response loss; it does not claim to simulate every native commit-error timing. |
| P2-07 | Coordinate simultaneous same-status HTTP requests against one real store; assert both successful results and retained state after reopening. Also check a mixed transition sequence cannot persist an illegal backward/skip result. |
| P2-10 | Test exclusive initialization refusal for existing files and startup refusal for missing, wrong-version/schema, corrupt and unwritable fixtures. Use deterministic controls where permissions differ by platform; never overwrite the fixture to pass a test. |
| P2-11 | Test domain restoration boundaries and invalid stored values with isolated raw-SQL fixtures; prove safe failure without repair, normalized replacement or fabricated state. |
| P2-12 | Run the documented commands locally and actual-engine tests on Linux CI; verify runtime files stay ignored, root/health remain unchanged and Domain.Tests need no database. |

Use unique disposable temporary directories per test, disable connection pooling, and dispose connections/hosts before deleting files. Keep test triggers and response-failure controls in test fixtures, with no production test routes or reset API. Update API fixtures that currently assume every host starts empty; a host's state will now come from its configured file. Replace dictionary-specific store tests with transaction/restoration tests rather than retaining a production fallback just to keep old fixtures unchanged.

Keep the existing format, warnings-as-errors, Release build and test gates. Add the child-process durability checks to reproducible automated verification; they cannot be replaced by in-memory hosts or one manual happy-path demonstration. Record the exact managed/native package versions and test limitations when implementing. These tests establish the defined local process-recovery behavior, not SQL Server semantics, power-loss certification or production readiness.

## Consequences and revisit triggers

The implemented workflow survives API restarts while keeping one deployable application and the existing HTTP surface. The cost is a native dependency, explicit local storage setup, blocking disk I/O, schema/version ownership, failure handling and additional process/integration tests. Data consumes disk without an enforced quota and remains accessible to whoever can access the local files.

Revisit before sharing storage between application processes, using network-mounted storage, deploying to ephemeral/multiple hosts, requiring backups or audit, handling sensitive data, adding materially more queries, or meeting a measured concurrency/latency target. Such requirements may justify SQL Server / Azure SQL, an ORM or a new boundary through another ADR. Acceptance of this decision does not authorize cloud resources or production deployment.

## Implementation evidence

[CaseDatabase](../../src/EnterpriseCaseIntelligence.Api/Cases/CaseDatabase.cs) implements configuration, exclusive initialization, schema/integrity checks and connection settings. [SqliteCaseStore](../../src/EnterpriseCaseIntelligence.Api/Cases/SqliteCaseStore.cs) owns operation ordering and transactions; [Case.Restore](../../src/EnterpriseCaseIntelligence.Domain/Cases/Case.cs) validates loaded values without changing their identity or text. No new production project or provider abstraction was needed.

The API pins Microsoft.Data.Sqlite **10.0.13**, resolving SQLitePCLRaw **2.1.13**. The bundled engine is SQLite **3.53.3**, verified locally; engine support is asserted and reported by the [lifecycle tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStoreLifecycleTests.cs); native loading and STRICT/transaction behavior are exercised locally and in Linux CI.

[Requirements coverage](../requirements/phase-2-durable-case-workflow.md#current-implementation-coverage) links each acceptance criterion to the tests. [Process recovery](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseProcessRecoveryTests.cs) forcibly terminates the API after acknowledged writes. [Storage failure tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStorageFailureTests.cs) use actual SQLite triggers/locks, and [response-loss tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseResponseLossTests.cs) suppress responses after committed operations. These do not simulate hardware power loss or every native commit-error timing.

The preceding decision sections record the selected implementation constraints; the [walkthrough](../usage/local-case-workflow.md) gives runnable commands.
