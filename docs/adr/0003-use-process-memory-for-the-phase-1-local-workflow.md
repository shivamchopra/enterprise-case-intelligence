# 0003: Use process memory for the Phase 1 local workflow

## Status

**Superseded for Phase 2 by [ADR 0004](0004-use-sqlite-for-the-durable-local-workflow.md).** Reviewed in PR #8 and implemented for Phase 1 in PR #9. The sections below preserve that historical decision; the current API uses SQLite.

## Context

The [reviewed workflow](../requirements/phase-1-case-workflow.md) needs create, retrieve-by-ID and status-update operations across requests in one running local application. At decision time, the domain model and tests existed and the API exposed only `/` and `/health`.

All data is synthetic. Durable recovery, multiple application instances and stale-client conflict detection are outside this first slice. Adding a database would introduce schema, configuration and operational work before any durability requirement has been established. The hosting layer can still receive concurrent requests, so shared mutable case state requires deliberate synchronization.

## Decision

For this local demonstration, implement one small concrete in-memory case store in the existing Api project. Register it through dependency injection as a **singleton per application host/service provider**. The store owns an instance-level `Dictionary<Guid, Case>` and one private synchronization gate.

The store supports only create, retrieve and change-status operations. Keep Domain independent of hosting and storage. Add no persistence project, repository interface, generic repository, database provider or external service for this slice. These boundaries can change when actual persistence requirements appear.

### Lifetime and data ownership

- Every new host starts with an empty store. No seeded cases, static dictionary or process-global state.
- Requests handled by the same host share that host's store. Request-scoped/transient stores would lose the intended continuity between create and retrieve requests.
- Data exists only in that host's memory. Restart, shutdown or process failure loses all cases; separate instances never share cases.
- No files, backups, expiration, eviction or capacity-management features are introduced. Retained cases consume memory for the host lifetime. Usage is limited to small synthetic demonstrations by scope; there is no enforced memory limit.
- The store creates and owns each mutable `Case`. Callers receive immutable snapshots containing the four case values, never the stored entity or backing collection. Changes go through store operations and the existing domain rules.

### Mutation and concurrency semantics

Use one short synchronous critical section for each operation, including reads:

1. **Create:** construct and validate a new domain case, add it without overwriting an existing ID, then capture its response snapshot.
2. **Retrieve:** look up the ID and capture a snapshot, or report absence.
3. **Change status:** perform lookup, call `Case.ChangeStatus`, and capture the resulting snapshot under the same gate. An unknown case reports absence; recognized cases retain the domain's no-op and rejection behavior.

All collection access and access to stored case status use that same instance gate. Never release it between lookup, transition validation and mutation. No mutable entity escapes for later HTTP serialization. Parse HTTP input before calling the store; perform no network/file I/O, awaits or response writing inside the critical section.

This serializes operations within one store. Invalid domain input or a rejected transition leaves stored data unchanged. Immediately repeating the current status succeeds. It does not add a version token or detect a client acting on an old read. Concurrent requests have no promised arrival order; each observes a valid serialized operation order. Snapshot values represent their operation's result even if a later request changes the case before the response is written.

Dependency injection does not make a shared singleton thread-safe, and a dictionary needs synchronization when reads and writes overlap. These constraints follow the official [.NET DI guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines#thread-safety) and [Dictionary thread-safety documentation](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0#thread-safety).

### HTTP and test boundaries

The HTTP layer validates JSON, route IDs and status strings and maps store/domain outcomes to the reviewed response contract. The store does not return HTTP results or expose request/response objects. Existing foundation endpoints retain their behavior and acquire no storage readiness check for an external service that does not exist.

The [store and API tests](https://github.com/shivamchopra/enterprise-case-intelligence/tree/4fde8995fc0cc6f326cf0ed35ba77e9a05bd48d8/tests/EnterpriseCaseIntelligence.Api.Tests/Cases) establish:

| Scenario | Required outcome |
| --- | --- |
| Create, retrieve, transition and retrieve in one host | The same ID and normalized text persist across requests; status reflects the successful transition. |
| Two cases with identical text | Distinct IDs and independent status; creating one never replaces another. |
| Invalid creation, unknown case or rejected transition | No partial creation or modification of another/stored case. |
| Obtain a snapshot, then update the case | The earlier snapshot remains unchanged; callers cannot mutate the stored entity. |
| Two simultaneous requests for `InReview` on a new case | Both succeed under transition/no-op rules; final status is `InReview` and ID/text remain unchanged. |
| Dispose one host and create another | The new host cannot retrieve the old host's IDs. |
| Run independent hosts in parallel | Cases and state do not leak between hosts. |

Each case integration test gets an independent initialized `WebApplicationFactory<Program>` host, while requests within a test reuse its client/host. Do not add a reset endpoint, static fixture store or test-order dependency. Use coordination primitives for concurrency tests rather than sleeps or assumed scheduling order. Existing pure domain tests continue to have no hosting dependency.

## Rationale

This is enough storage to exercise the reviewed HTTP workflow while preserving the current two-project production boundary. It requires no credentials, database installation or additional runtime package. One gate is easy to inspect and sufficient for a small local demo; finer-grained locking would add complexity without a demonstrated throughput requirement.

Immutable snapshots protect the synchronization boundary without changing the domain model for serialization. The decision deliberately trades durability and multi-instance operation for a smaller first runnable workflow.

## Alternatives Considered

| Alternative | Reason deferred or rejected for this slice |
| --- | --- |
| SQL Server / Azure SQL or another relational database | A candidate for future durable transactional requirements; schema, provisioning and integration setup are unnecessary for the current local requirement. No product is selected here. |
| SQLite or a JSON file | Adds durable file lifecycle, crash/recovery and concurrent-write concerns. Durability is not required by this slice. |
| EF Core InMemory provider | Adds an ORM/provider dependency and a database-shaped abstraction to an intentionally non-durable demo. |
| Static dictionary | Couples independent hosts/tests and obscures ownership and lifetime. |
| Scoped/transient store | Cannot provide shared case state across separate requests without another shared owner. |
| ConcurrentDictionary alone | Collection atomicity does not cover the whole lookup/domain-transition/snapshot operation on a mutable case. One explicit gate defines that boundary; see the [ConcurrentDictionary operation guarantees](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2?view=net-10.0#remarks). |

## Consequences and revisit triggers

The demo will lose data on restart and cannot support multiple instances, durable audit, backups or production use. The single gate limits throughput; synthetic data remains in memory until the host ends. Document these limits prominently when the API/store are implemented. No production durability, authorization or data-protection capability is implied.

Revisit this ADR before requiring data to survive restart, deploying multiple instances, handling real/sensitive records, supporting durable audit/integration, detecting stale writes or meeting a measured throughput/memory requirement. A later ADR must define the needed persistence, consistency, security and migration behavior. It may replace this concrete store; this decision does not promise a drop-in database replacement.

## Historical implementation

[InMemoryCaseStore](https://github.com/shivamchopra/enterprise-case-intelligence/blob/4fde8995fc0cc6f326cf0ed35ba77e9a05bd48d8/src/EnterpriseCaseIntelligence.Api/Cases/InMemoryCaseStore.cs) owns the dictionary and synchronization gate; [CaseSnapshot](https://github.com/shivamchopra/enterprise-case-intelligence/blob/4fde8995fc0cc6f326cf0ed35ba77e9a05bd48d8/src/EnterpriseCaseIntelligence.Api/Cases/CaseSnapshot.cs) holds immutable result values. The composition root registers the store once per host. [Case endpoints](https://github.com/shivamchopra/enterprise-case-intelligence/blob/4fde8995fc0cc6f326cf0ed35ba77e9a05bd48d8/src/EnterpriseCaseIntelligence.Api/Cases/CaseEndpoints.cs) validate input and map outcomes without exposing stored entities.

No new production project, package, repository abstraction or external service was added. The [local walkthrough](https://github.com/shivamchopra/enterprise-case-intelligence/blob/4fde8995fc0cc6f326cf0ed35ba77e9a05bd48d8/docs/usage/local-case-workflow.md) documents data loss and other limits. Durable persistence and production deployment remain future decisions.
