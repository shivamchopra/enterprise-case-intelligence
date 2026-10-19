# Phase 2 requirements: durable local case workflow

**Status: Reviewed requirements — durable local workflow implemented.** Reviewed in PR #10; the storage decision was reviewed in PR #11. [ADR 0004](../adr/0004-use-sqlite-for-the-durable-local-workflow.md) is accepted and implemented.

The [Phase 1 workflow](phase-1-case-workflow.md) originally used temporary host-owned memory; Phase 2 now uses a retained SQLite file. These requirements add one capability: resume the same synthetic case workflow after restarting the application. They preserve the case contract while adding restart continuity. [ADR 0003](../adr/0003-use-process-memory-for-the-phase-1-local-workflow.md) is the superseded historical decision.

## Purpose and scenario

A fictional service-desk staff member creates a case, starts reviewing it, stops the local API and returns later. Restarting the API against the same retained store must preserve the case's identifier, normalized text and latest committed status. Today, the restart loses that work.

This is a reference-project assumption for review, not a validated enterprise requirement. Restart recovery is the reason to evaluate persistence; it does not establish a need for cloud hosting or independent services.

## Scope and operating assumptions

- One local API process uses one configured durable store at a time. A later run can reopen that store. Concurrent application processes sharing a store are outside this slice.
- Only synthetic cases are allowed. This adds no authentication, authorization or permission boundary for real records.
- Storage remains available and intact across application shutdown or process termination. Surviving machine power loss, hardware failure, storage deletion/corruption or loss of the storage service is outside the recovery guarantee.
- Existing cases in the Phase 1 in-memory store are disposable. There is no import or migration of that live memory into the new store.
- The three case routes and domain rules remain the user workflow. Listing, search, text editing, deletion, audit history, timestamps, assignment and new lifecycle states remain deferred.

ADR 0004 selects SQLite with Microsoft.Data.Sqlite in the existing Api project, without an ORM or provider interface. SQL Server / Azure SQL remain candidates for future server-storage requirements. Azure resources, containers, messaging, Python/AI, frontend, observability integrations, backups, high availability and multi-instance operation are outside this slice.

## Behavioral requirements

### Preserve the case contract

Keep the [Phase 1 HTTP contract](phase-1-case-workflow.md#http-contract): the same routes, four-field representation, strict input validation, generated UUIDs, `Location`, lifecycle matrix and safe problem responses. No persistence metadata or provider details appear in a case response. Title and description remain immutable, and a restored case must retain its original ID and status without replaying artificial user transitions.

The existing domain boundary remains independent of HTTP and storage technology. A new case still starts in `New`; loading an existing case is a distinct operation, with persisted values validated rather than silently assigned defaults. Invalid or unsupported stored state must produce a failure instead of a fabricated case, fresh identifier or reset status.

### Commit before reporting success

- A successful `POST /cases` response means the complete case has committed to the durable store. A later run against that store can retrieve the returned ID with the same text and latest committed status.
- A successful status response means the requested status has committed, or the committed case already has that status. A later read or restart observes that status unless a subsequent valid request changed it.
- Creation and each status operation are atomic for one case. Lookup, transition validation and update must prevent concurrent requests from bypassing domain rules or overwriting a later status with an earlier one. An in-memory lock alone is not evidence that a storage commit succeeded.
- Validation failures, missing cases and rejected transitions leave committed state unchanged. A storage failure known to occur before commit leaves the previous committed state intact and creates no partial case.
- Same-status requests remain successful no-ops, including after restart. Distinct successful creates with identical text remain separate cases. There is no creation idempotency key or stale-client/version-token feature.

Operations have a valid serialized order within the supported single application process; request arrival order is not promised. For example, two concurrent `InReview` requests for one new case both succeed, with the second observing an allowed no-op. Responses are snapshots of their own operation and cannot change during later serialization.

### Distinguish failure from an uncertain outcome

If storage cannot complete an operation, do not return success, report a missing case, or fall back to volatile memory. Retain the existing generic `500 Internal Server Error`, `application/problem+json`, `about:blank` and `internal_error` response contract for unexpected storage failures. Do not expose submitted text, storage locations, connection details, provider messages or stack traces.

A commit can complete before the process stops or the client receives its response. A lost response, cancellation or uncertain commit outcome does **not** prove the write rolled back. If the client knows the ID, it can retrieve the case to inspect the current state. Retrying the current status remains a no-op; a later state can still cause the existing `409 invalid_transition`. Retrying an unacknowledged create can create a second case because creation deduplication is outside scope. No exactly-once guarantee is made.

### Store lifecycle and startup

Provide an explicit, documented way to initialize a fresh local store and then start the API against it. Initializing a fresh store must not overwrite an existing one. Normal application startup must not silently create a replacement store, erase existing records, seed examples or repair incompatible/corrupt data by resetting it.

A missing, unavailable, incompatible or unreadable configured store prevents startup from accepting case requests. Explain how to correct configuration or initialize a store deliberately; automatic destructive recovery is not part of this slice. If storage becomes unavailable after startup, affected case requests fail safely as above. Recovery may require restarting the API against the same intact store; automatic retries and transparent reconnection are not required.

Keep `GET /` and `GET /health` behavior unchanged when the host is running. `/health` remains a host liveness check and makes no promise about storage readiness. Do not add new health routes as part of this requirement.

Document local setup, configuration, initialization, restart and deliberate disposal of synthetic data. Keep runtime data and machine-specific configuration out of Git; never commit credentials. Technology-specific setup and supported storage versions belong to the later implementation and ADR.

## Acceptance criteria

These criteria describe the implemented local workflow; the coverage table below links the automated evidence and limitations.

| ID | Given / when | Required evidence |
| --- | --- | --- |
| P2-01 | Explicitly initialize a fresh store, start the API, create a case and retrieve its `Location` | Phase 1 ID, normalization, `New`, four-field response and `201` behavior hold using the selected durable store. |
| P2-02 | Create cases, advance one to `InReview` and another to `Closed`, then stop and recreate the host against the same store | All original IDs, text and latest committed statuses survive; another case remaining `New` is unchanged. |
| P2-03 | A separate API process acknowledges creation/status requests and is then forcibly terminated without graceful shutdown | A new process using the same retained store reads all acknowledged writes. Do not substitute reusing an in-memory test host for this check. |
| P2-04 | Exercise all nine lifecycle matrix cells and strict Phase 1 input/media-type failures with durable storage | Existing success/error contracts hold; rejected writes leave committed state unchanged, including after reopening the store. |
| P2-05 | Force controlled storage failures before commit, testing both creation and transition | No success or partial record; generic safe failure; an independent read after recovery/reopen observes the previous committed state. |
| P2-06 | A commit succeeds but the response is lost before reaching the client | Reopening confirms committed state; retrieval by a known ID reconciles it. A same-status retry succeeds. No claim that a failed/lost response implies rollback. |
| P2-07 | Two coordinated requests ask a new case for `InReview` concurrently | Both succeed; a later read and restart show `InReview`, unchanged ID/text and no partial state. |
| P2-08 | Create two cases with identical text, change only one and reopen the store | Distinct IDs and independent durable state; no accidental deduplication or overwrite. |
| P2-09 | Reuse a retained store across hosts; separately run hosts with independently initialized stores | Reuse preserves cases; independent stores never leak state. Creating another case after restart preserves existing IDs and records. |
| P2-10 | Initialize an existing store, or normally start with missing/unavailable/incompatible storage | Initialization cannot overwrite it; startup cannot silently reset or fall back to memory. Valid existing data is retained. |
| P2-11 | Attempt to load invalid persisted case values, using an isolated fixture | Fail safely with no invented ID/default status and no silent destructive repair. No production endpoint for test injection or store reset is added. |
| P2-12 | Follow the documented setup/restart sequence from a clean checkout and run all quality gates | A synthetic workflow is reproducible; root/health and domain tests still pass; no runtime data, credentials or machine paths enter Git. |

## Verification boundaries

Keep pure domain tests independent of a storage provider. Add integration tests against the **actual chosen storage engine**, with independently initialized disposable stores and deterministic cleanup. An in-memory substitute cannot prove transaction, commit or restart behavior. Use coordination/fault controls rather than sleeps; inject failures without production-only test routes.

Separate application-process termination coverage from graceful host replacement. Keep retained storage alive during the process-termination test and wait for acknowledged writes before terminating the API. Do not claim this proves hardware or power-loss recovery. The ADR must explain how pre-commit and lost-response outcomes are exercised and what evidence the tests cannot establish.

The later implementation must define reproducible local and CI storage setup. Existing format verification, warnings-as-errors, Release build, domain tests and foundation endpoint checks remain required. No cloud account, real data or committed secret may be required; any new local tooling prerequisite must be explicit and justified by the storage decision.

## Decision and implementation sequence

1. **Reviewed in PR #10:** restart continuity and the operating/failure assumptions form the requirements baseline for this slice.
2. **Accepted and implemented in ADR 0004:** evaluate options against these criteria, including setup cost, durability limits, transaction/concurrency behavior, initialization/schema compatibility, safe failures and actual-engine testability. Explain configuration, domain rehydration and any necessary boundary changes. Supersede ADR 0003 only when the replacement decision is accepted; document implementation status accurately.
3. **Implemented:** introduce only the storage, domain-loading support and test/configuration changes that decision needs, preserving the case HTTP contract.
4. **Verified with automated tests and documented commands:** run the acceptance checks and update the walkthrough, architecture and README with implemented behavior and remaining limits.

The durable local workflow is implemented. The recovery guarantee excludes machine power/storage loss, backups and multi-instance operation. Production deployment and handling real records remain outside scope.

## Current implementation coverage

| Criteria | Automated evidence |
| --- | --- |
| P2-01, P2-04 | Existing [creation](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseCreationEndpointTests.cs), [status](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStatusEndpointTests.cs) and [lookup](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseLookupAndIsolationTests.cs) HTTP tests now run against real SQLite files. |
| P2-02, P2-07–09 | [Durability tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseDurabilityTests.cs): every retained status, identity/text, snapshots, duplicate independence, concurrent transitions and creation after restart; lookup tests isolate separate files. |
| P2-03 | [Process recovery test](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseProcessRecoveryTests.cs): explicit initialization, three acknowledged statuses, forced API termination and retrieval in a fresh process using the retained file. |
| P2-05 | [Storage failure tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStorageFailureTests.cs): aborting triggers with side effects, unexpected update counts, lock timeout and missing runtime storage; no partial writes or misleading conflicts. |
| P2-06 | [Response loss tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseResponseLossTests.cs): suppress successful create/status responses, independently reopen committed state and reconcile/retry a known ID. This does not simulate every native commit-error timing. |
| P2-10 | [Lifecycle tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Cases/CaseStoreLifecycleTests.cs): initialization refusal, missing configuration/files, corrupt/empty files, unsupported versions/schema/constraints, WAL and a blocked journal path that deterministically prevents writes. |
| P2-11 | [Domain restoration tests](../../tests/EnterpriseCaseIntelligence.Domain.Tests/Cases/CaseRestorationTests.cs) and storage failure tests validate loaded values and reject damaged state without repair/defaults. |
| P2-12 | [Walkthrough](../usage/local-case-workflow.md), existing root/health tests, [CI](../../.github/workflows/ci.yml) and runtime-file ignore rules; no external service or secret required. |

The package and native engine versions are recorded in ADR 0004 and lifecycle-test output. Unit tests remain independent of SQLite. Real-engine tests prove the specified local application-process behavior, not power-loss certification, backup recovery or production readiness.
