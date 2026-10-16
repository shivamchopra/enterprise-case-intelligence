# Architecture overview

## Context

Enterprise case modernization can involve fragmented legacy processes, information boundaries and manual coordination. This reference project uses generic synthetic concepts. It does not reproduce any employer's or client's system.

## Architecture Objectives

Maintainability, evolvability, security, reliability and testability guide current choices. Cloud readiness and structured observability are future objectives. Decisions should expose their tradeoffs rather than assume theoretical scale.

## Current Architecture

One ASP.NET Core .NET 10 Minimal API is the only deployable application. It exposes the foundation `/` and `/health` routes plus case creation, retrieval and status changes. Integration tests run the real application in isolated in-memory hosts.

The Domain library implements case creation, text validation and the `New → InReview → Closed` lifecycle. Focused unit tests exercise these rules directly. The API owns a singleton in-memory case store, scoped to its host. One private gate protects collection access and domain mutations; callers receive immutable snapshots.

```mermaid
flowchart LR
    ApiTests[Api.Tests] -->|WebApplicationFactory| Api[Api: HTTP endpoints]
    Api --> Store[Api: host-owned in-memory store]
    Store -->|project reference| Domain[Domain: case rules]
    DomainTests[Domain.Tests] -->|unit tests| Domain
```

## Current Boundaries

- **Api:** composition root, dependency injection, foundation/case endpoints and a concrete host-owned store. Input validation, HTTP error mapping and serialization remain here. No stored mutable entity escapes the store.
- **Domain:** a `Case` model and `CaseStatus` enum. Creation generates an identifier, trims and validates required text and starts the case in `New`. Identifier and text are immutable. Status can move forward or remain unchanged; invalid values/transitions are rejected before mutation.

The domain uses standard argument exceptions for invalid input and `InvalidOperationException` for a disallowed transition. These contain no submitted case text. The API maps these outcomes to the reviewed HTTP contract; the domain does not know HTTP status codes or problem details. A fallback exception handler emits a generic problem response for unexpected failures.

## Dependency Direction

Api references Domain. Api.Tests references Api. Domain.Tests references Domain directly. Domain references neither Api nor tests and has no web, storage or infrastructure dependencies. Additional boundaries will be introduced only when actual implementation needs them.

## Storage lifetime and concurrency

The store starts empty for each host, retains cases in memory until that host ends and shares no state between instances. Reads and lookup/transition/snapshot operations use one synchronous gate. Snapshots preserve an operation's values even if a later request changes the case. Independent test hosts have independent stores.

This local demo has no durable recovery, capacity limit, expiration or stale-client/version-token protection. Concurrent request arrival order is not guaranteed. It is for small synthetic demonstrations, with no production persistence or authorization capability. See [ADR 0003](../adr/0003-use-process-memory-for-the-phase-1-local-workflow.md).

## Constraints

The local case workflow uses no external infrastructure, secrets or real business data. The SDK is pinned to an installed stable .NET 10 baseline, with stable feature-band roll-forward within .NET 10.0. SLNX is created through normal CLI tooling. One CI workflow enforces formatting, warnings-as-errors, Release compilation and tests.

## Assumptions

These are working assumptions, not proven requirements:

- A case-centric use case will be small enough to begin in one application.
- Relational persistence may suit future transactional requirements.
- Integration and AI capabilities may eventually justify specialized components.

Each assumption will be revisited with concrete requirements and evidence.

## Non-goals

This slice does not implement durable persistence, cloud resources, containers, messaging, identity, authorization, a frontend, Python services, AI, production deployment or telemetry integration. It does not define a distributed system or promise a final modular-monolith topology.

## Security Principles

- No secrets, real case records, personal information or confidential material in source or contributions.
- Minimize exposed metadata; no environment, machine, build or host identifiers.
- Prefer secure defaults and least privilege; CI has read-only repository permissions.
- Future authorization and data-access boundaries must be designed before handling sensitive information.

The present API is a local synthetic workflow without authentication. These principles do not constitute implemented production security controls.

## Operability Principles

Framework health checks and automated build/test gates exist now. The health endpoint checks no external dependencies and reports host health only. Structured observability, dependency readiness checks and reproducible deployment are future work, to be defined alongside real infrastructure.

## Evolution Philosophy

Start with one small deployable application and explicit internal boundaries. Add abstractions, processes or independent services when requirements justify deployment independence, scaling differences, reliability, ownership, security or operational isolation. Avoid optimizing for hypothetical scale. Record meaningful decisions in [ADRs](../adr/README.md).

The [reviewed Phase 1 requirements](../requirements/phase-1-case-workflow.md) are implemented for the local workflow. [ADR 0003](../adr/0003-use-process-memory-for-the-phase-1-local-workflow.md) records the current temporary storage choice. Further persistence, consistency or deployment requirements must be evaluated before replacing this small local demonstration.

## Conceptual Future Direction — Not Yet Implemented

The following relationships are possibilities, not selected services or deployed components:

```mermaid
flowchart TD
    App[Future .NET application/API]
    App -.-> Persistence[Potential relational persistence]
    App -.-> Integration[Potential asynchronous/event-driven integration]
    App -.-> Hosting[Potential Azure hosting/services]
    App -.-> Python[Potential Python AI capability]
    Python -.-> Evaluation[Potential retrieval/model/evaluation capabilities]
```

No cloud service, database product, broker or model provider is committed by this diagram. SQL Server / Azure SQL may be evaluated for future persistence. Infrastructure as Code, identity, observability, AI evaluation and human approval boundaries require future requirements and decisions.
