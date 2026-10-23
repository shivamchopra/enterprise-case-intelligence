# Project roadmap and priority assessment

**Status: Planning direction.** The shipped baseline is the Phase 5 local HTTP workflow with EF Core Code First and format 4. Future milestones below describe outcomes, not implemented features or selected cloud services.

## Assessment

The project aims to demonstrate credible enterprise modernization through runnable .NET code, useful workflows, explicit tradeoffs and evidence. Persistence now has durable writes, retained history, retry-safe creation, explicit upgrades and substantial failure/recovery coverage. EF adoption is complete. Further storage work should serve a product or operating requirement.

The largest immediate demonstration gap is the user journey: a reviewer needs curl and multiple commands to perform a simple service-desk task. A local browser workspace would connect the existing capabilities into an accessible demonstration without inventing more domain rules or infrastructure. This scenario remains a synthetic reference-project assumption, not validated enterprise research.

| Area | Current evidence / gap | Priority implication |
| --- | --- | --- |
| Case workflow | Create, browse/filter, retrieve, progress status and inspect history work through HTTP. | Demonstrate the complete journey next; then select a concrete business enhancement. |
| User experience | Only the command-line walkthrough exists; no browser interface. | Add a small local workspace using the existing API. |
| Data and reliability | EF Code First, retained SQLite state, atomic history/key writes and recovery tests are implemented. | Maintain these guarantees during features; avoid making storage hardening the default backlog. |
| Security and hosting | No identity, authorization or deployment; one local process/file. | Establish explicit security and operating requirements before broader access or hosted use. |
| Operations | Liveness and offline inspection exist; runtime storage readiness and structured observability do not. | Revisit readiness and operational diagnostics with the hosting milestone. |
| Integration and AI | No external system, event workflow, retrieval or evaluation is implemented. | Define a useful scenario and acceptance evidence before selecting services or models. |

## Ordered direction

1. **Next: local browser case workspace.** Deliver browse/filter → create → review details/history → progress status against retained local data. Keep one application host and the current API, domain and storage contracts. [Phase 6 requirements](requirements/phase-6-case-workspace.md) define the bounded slice. Completion evidence should include real-browser workflow tests and a reproducible local walkthrough.
2. **Business workflow expansion.** Use the completed journey to choose one concrete service-desk need, such as prioritization, ownership or finding work. Define its actor, rules and failure cases first; these examples are candidates, not commitments to implement every field. Use EF Code First migrations if the selected capability changes persisted data.
3. **Secure, operable hosting.** Define identity and authorization, expected users/data, deployment/access boundaries, diagnostics and recovery responsibilities together. Revisit the [deferred readiness proposal](requirements/phase-6-storage-readiness.md). Evaluate Azure and server-managed storage against those needs; the current single-process SQLite model does not establish shared-hosting support. Do not expose this unauthenticated demonstration as a production service.
4. **A concrete integration workflow.** Select a synthetic producer/consumer scenario and define delivery, duplicate handling, failure/reconciliation and observability requirements before choosing a broker or splitting services.
5. **An evaluated AI assistance scenario.** Choose a task that helps the case journey, with synthetic source material, expected answers, evidence/citations, failure/abstention tests and human review before consequential actions. Python, retrieval and model/provider choices follow that scenario.

This ordering can change when a concrete dependency or user need appears. Identity and access boundaries must precede any feature that depends on identifying staff; they cannot be postponed merely to follow the list. Each milestone needs its own requirements and proportionate architecture decisions. No empty projects, speculative services or technology demonstrations are required to fill the roadmap.

## Working rule

Choose the next slice by user outcome, demonstration value and actual dependencies. Maintain tests and public documentation as behavior ships. EF Core Code First is the persistence implementation preference, not a product roadmap. Keep future capabilities separate from current evidence in the README and profile.
