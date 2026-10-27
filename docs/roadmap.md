# Project roadmap and priority assessment

**Status: Phase 7 and the Phase 8 identity/application-ingress slices delivered; hosted acceptance pending.** The current baseline adds configured OIDC/Reader/Caseworker sessions to title search in the browser/API workflow, preserving EF Core Code First and format 4. Later milestones below describe outcomes, not implemented features or selected cloud services.

## Assessment

The project aims to demonstrate credible enterprise modernization through runnable .NET code, useful workflows, explicit tradeoffs and evidence. Persistence now has durable writes, retained history, retry-safe creation, explicit upgrades and substantial failure/recovery coverage. EF adoption is complete. Further storage work should serve a product or operating requirement.

The Phase 6 priority was the user journey: a reviewer previously needed curl and multiple commands to perform a simple service-desk task. The local browser workspace now connects the existing capabilities without inventing more domain rules or infrastructure. This scenario remains a synthetic reference-project assumption, not validated enterprise research.

| Area | Current evidence / gap | Priority implication |
| --- | --- | --- |
| Case workflow | Find cases by literal title/status, create, retrieve, progress and inspect history through HTTP and the browser workspace. | Case finding delivers the first business enhancement; revisit additional rules when a concrete scenario needs them. |
| User experience | The same-host workspace and command-line walkthrough demonstrate the journey. | Maintain browser/recovery coverage as business behavior evolves. |
| Data and reliability | EF Code First, retained SQLite state, atomic history/key writes and recovery tests are implemented. | Maintain these guarantees during features; avoid making storage hardening the default backlog. |
| Security and hosting | OIDC/cookie, role policies, CSRF and same-host proxy tests, plus private local Entra role/revocation evidence; no hosted deployment; one process/file. [Phase 8 requirements](requirements/phase-8-secure-hosting.md) propose invited shared-workspace access. | Next, assess a compatible zero-recurring-cost platform/storage/ingress, then finish hosted provider/user checks; hosting remains unimplemented. |
| Operations | Liveness and offline inspection exist; readiness, hosted diagnostics and backup/restore are proposed, not implemented. | Select compatible hosting/storage and prove operating/recovery requirements before inviting users. |
| Integration and AI | No external system, event workflow, retrieval or evaluation is implemented. | Define a useful scenario and acceptance evidence before selecting services or models. |

## Ordered direction

1. **Completed: local browser case workspace.** Browse/filter → create → review details/history → progress status against retained local data. One application host and the existing API, domain and storage contracts remain. [Phase 6 coverage](requirements/phase-6-case-workspace.md#current-implementation-coverage) links real-browser tests and the reproducible local walkthrough.
2. **Completed: focused business enhancement.** [Phase 7 title search](requirements/phase-7-case-title-search.md#current-implementation-coverage) lets staff find known synthetic work without traversing every page. Further needs such as prioritization/ownership remain candidates; define actor/rules/failure cases first. Use EF Code First migrations if a selected later capability changes persisted data.
3. **Next milestone — proposed, not implemented: secure review hosting.** [Phase 8 requirements](requirements/phase-8-secure-hosting.md) define invited Reader/Caseworker access to one shared synthetic workspace, browser/API session and CSRF behavior, ingress/configuration boundaries and proposed recovery targets. [ADR 0009](adr/0009-authenticate-the-shared-case-workspace.md) and the [identity runbook](usage/authenticated-case-workspace.md) now supply the locally testable authenticated slice. [ADR 0010](adr/0010-trust-a-single-loopback-https-proxy.md) now adds tested same-host proxy support. The [hosting assessment](tasks/review-hosting-preparation.md) records the conditional VM/local-file option, and [ADR 0011](adr/0011-use-entra-for-private-review-identity.md) selects Entra with [private local role/revocation evidence](usage/entra-review-verification.md#local-verification-on-2026-10-26). [ADR 0012](adr/0012-gate-cloud-hosting-on-zero-recurring-cost.md) now keeps review local while [zero-cost App Service F1/SQL compatibility](tasks/zero-cost-review-hosting.md) is assessed. Next define managed-host security/provider requirements, prove operations and finish hosted identity checks, revisiting [readiness](requirements/phase-6-storage-readiness.md) against that actual design. Azure/server-managed storage remain candidates; current SQLite evidence does not establish network-share, ephemeral or multi-instance support. No compute resources or public deployment are selected by the proposal.
4. **A concrete integration workflow.** Select a synthetic producer/consumer scenario and define delivery, duplicate handling, failure/reconciliation and observability requirements before choosing a broker or splitting services.
5. **An evaluated AI assistance scenario.** Choose a task that helps the case journey, with synthetic source material, expected answers, evidence/citations, failure/abstention tests and human review before consequential actions. Python, retrieval and model/provider choices follow that scenario.

This ordering can change when a concrete dependency or user need appears. Identity and access boundaries must precede any feature that depends on identifying staff; they cannot be postponed merely to follow the list. Each milestone needs its own requirements and proportionate architecture decisions. No empty projects, speculative services or technology demonstrations are required to fill the roadmap.

## Working rule

Choose the next slice by user outcome, demonstration value and actual dependencies. Maintain tests and public documentation as behavior ships. EF Core Code First is the persistence implementation preference, not a product roadmap. Keep future capabilities separate from current evidence in the README and profile.
