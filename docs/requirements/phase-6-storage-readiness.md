# Deferred proposal: bounded local-store readiness

**Status: Deferred requirements — not implemented.** Originally proposed for Phase 6, this is retained for a future hosting/operability slice. The [project reassessment](../roadmap.md) prioritizes the [local browser workspace](phase-6-case-workspace.md) instead. The filename and P6 criterion IDs are retained for traceability to the reviewed proposal; they do not reserve the next phase.

The proposal was written against Phase 5 with EF Core Code First and format 4; the current browser workspace reuses that storage baseline. This proposes a separate storage-readiness endpoint; `/health` currently reports host liveness only.

## Assessment and outcome

The current [storage-failure test](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStorageFailureTests.cs) deliberately moves the database away after hosting starts. Case requests fail safely while `/health` still returns `200`. That preserves liveness, but a local operator has no small HTTP probe to distinguish a responsive host from an available current-format store. Offline `inspect-store` provides deeper validation and requires store users stopped.

Add `GET /health/ready` for a fresh, bounded observation of the configured store and a rollback-only write probe. Retain `/health` as storage-independent liveness. This serves the existing local operator/curl workflow; no external monitoring system, orchestrator, deployment or production-readiness claim is selected.

## HTTP contract

| Request / observation | Required response |
| --- | --- |
| `GET /health` with an otherwise responsive host | Existing `200` and plaintext `Healthy`, including while storage is unavailable or readiness is probing/failing. Run no storage check for this route. |
| `GET /health/ready` when all readiness checks succeed | `200`, UTF-8 `text/plain`, body exactly `Healthy`. |
| `GET /health/ready` when a check fails, times out or cannot acquire required resources within its documented finite wait | `503`, UTF-8 `text/plain`, body exactly `Unhealthy`. No partial/degraded success. |

Both readiness outcomes must prevent response caching, including a `Cache-Control` directive of `no-store`. Expose no check descriptions, exception messages, SQL, paths, IDs, keys, case text, schema dump or dependency details. Do not add a JSON diagnostics envelope, Location or per-check data. Unknown reasons must not become a healthy fallback.

Only the server-configured store is eligible. Request headers/query values must not select a file, alter validation depth or enable debug/reset/repair behavior. Existing root and case contracts, status codes and validation-before-storage behavior remain unchanged.

## Meaning and limits of readiness

A successful observation must establish, within a consistent operation:

1. The configured existing local file opens with the supported connection/journaling/synchronization/FK settings. A missing file must remain missing.
2. The application format is the currently supported format 4; its bounded schema/migration-metadata checks succeed. Retained formats 1–3, extra/missing/incompatible objects, unknown/invalid EF migration records and model/snapshot drift fail safely. Merely opening SQLite or calling `CanConnect` is insufficient.
3. A write reservation and rollback-only metadata/write-access probe succeed. Always roll back and release owned resources before publishing success. Do not create a case/event/binding, call the event clock, commit a transaction or advance the store format.

This is a point-in-time availability signal. A subsequent request can encounter contention or a storage failure. It does not reserve capacity, prove future commits, certify every case/history/binding, establish free disk capacity or supply backup/power-loss guarantees. A contended/unwritable store can be temporarily not ready.

Keep request work independent of the case/key collection size: inspect only fixed format/schema and bounded migration metadata, without loading case/history/key rows, scanning those tables or running whole-store integrity/FK scans. Do not reuse a deep validation method if it performs those scans. Offline `inspect-store` retains full validation; normal startup keeps its existing validation. Damaged unrelated case/history data can therefore fail a case request/deep inspection while the narrowly defined readiness probe succeeds. Document and test that boundary. Bounds cover application materialization and operation counts, without promising provider-internal scan cost or latency, especially during native recovery.

## Resource ownership and failure recovery

Use the existing EF-first persistence boundary and framework health-check capabilities. Keep ordinary metadata queries in EF where supported; justify any provider-only schema/PRAGMA/write-probe SQL in the implementation decision. Add no application table, migration, schema version, production project or external health service for this slice.

Choose and document finite provider and gate/lock waits. Bound the number of commands and rows materialized per probe. Avoid an unbounded wait behind the case-operation gate, retry loops and stale cached success/failure. Account for SQLite's synchronous I/O behavior; do not claim a health-check timeout cancels native work, or hide continuing database work behind an abandoned background task. This is a finite-work/wait requirement, without a hard real-time latency claim.

Every probe owns/disposes its context, connection, transaction and any gate lease. Success, failure and caller cancellation must leave no application-owned work running and release resources according to the documented provider limits. Under contention, readiness must not damage/interleave with normal atomic case/history/key writes. Registering readiness must not accidentally make `/health` run all registered dependency checks.

After a transient failure, the next request performs a new observation against the same configured file. Restoring an available intact supported store can return to `200` without an application-managed reset or a cached failed state. Do not initialize, migrate, seed, mark an EF baseline, repair metadata, clear locks, delete journals or switch to another file. Native SQLite journal recovery may occur; that does not authorize application repair.

For an idle, intact test store, repeated success/failure probes must preserve committed file bytes and logical case/history/key/migration values. Do not promise unchanged filesystem timestamps or suppress SQLite's own recovery behavior.

## Acceptance criteria

| ID | Given / when | Required result |
| --- | --- | --- |
| P6-01 | Probe `/health` before/after readiness registration and during a controlled storage failure | Existing `200`/`Healthy`, no storage probe on liveness; root metadata unchanged. |
| P6-02 | Probe an initialized empty or populated current-format store | Readiness returns exact `200`/`Healthy`, plain UTF-8, non-cacheable, with no details or extra data. |
| P6-03 | Remove/replace the file, deny journal/write access or hold a competing SQLite transaction | Readiness returns safe `503`/`Unhealthy` within documented finite waits; no missing-file creation, success fallback or application mutation. |
| P6-04 | Probe repeatedly, including with a throwing event clock | No event-clock read, case/history/key change, committed metadata change or format advance; quiescent intact file bytes remain equal. |
| P6-05 | Coordinate a readiness probe waiting/failing under contention alongside liveness and case operations | Liveness stays storage-independent; bounded gate/resource ownership is proven and existing atomic-write/replay invariants hold. |
| P6-06 | Restore the same intact store or release a controlled lock after a failed probe | A fresh readiness request can return `200` without host replacement/reset, stale cache or lingering resource lease. |
| P6-07 | Cause errors containing synthetic sensitive markers | Response remains only `503`/`Unhealthy`; no path, SQL, exception/check descriptions, IDs, keys or record values. Both response outcomes have `no-store`. |
| P6-08 | Exercise success, provider failure and caller cancellation with deterministic fixture coordination | Transaction rolls back and resources are released within documented provider limits; no detached task/retry, commit, migration or repair. |
| P6-09 | Probe a multi-batch data set and separately damage unrelated case/history data without changing the supported schema | Probe work stays limited to fixed schema/metadata and the rollback check, with no collection/deep-integrity scan. Deep inspection still detects the bad data; readiness is not an integrity certificate. |
| P6-10 | Use an actual API child process; temporarily make its file unavailable, then restore it | Observe `200 → 503 → 200` on readiness, `/health` stays `200`, and retained case/history/key/migration values remain unchanged. |
| P6-11 | After startup, substitute a frozen version-1/2/3 store or alter schema/EF metadata | Readiness refuses with `503` without adoption/repair; old source data and version remain intact. Existing offline commands retain their behavior. |
| P6-12 | Run local/CI gates and the entire existing suite plus readiness coverage | Existing 415 tests remain exercised; restore/tool restore, formatting, zero-warning Release build, EF model/snapshot check and all tests pass. Format 4/migration model remain unchanged. |

## Implementation sequence and evidence

When hosting requirements justify this work, revisit this contract, then document the focused probe/transaction/timeout design and its SQL exceptions before implementation. Keep this separate from the full offline inspector and current startup validation. Implementation must use disposable real SQLite files, bounded loopback child-process tests and test-only coordination; no production fault routes or arbitrary sleeps.

When implemented, update the README and walkthrough with the two probes, healthy/unhealthy examples, limits and recovery behavior. Keep the profile/implemented-scope evidence on the shipped Phase 5 capability until then. These requirements add no runnable endpoint, package, table or migration today.

Microsoft's [ASP.NET Core health-check guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0) describes separate liveness/readiness checks, endpoint filtering, status mapping and minimal output. The contract above is this project's selected local behavior, not a claim that framework connectivity checks provide its format/write guarantees.
