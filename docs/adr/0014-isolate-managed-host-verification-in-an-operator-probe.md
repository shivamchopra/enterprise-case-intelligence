# 0014: Isolate managed-host verification in an operator probe

- Status: Accepted — standalone probe implemented/tested locally; cloud acceptance pending
- Date: 2026-10-28

## Context

The [managed-ingress contract](../tasks/managed-ingress-verification.md) needs original transport/header and protected-key retention evidence before a platform trust mode can be selected. The case API correctly refuses an unproven backend. Adding a temporary bypass there would weaken the very boundary being assessed. [ADR 0012](0012-gate-cloud-hosting-on-zero-recurring-cost.md) also prohibits treating introductory credit as a sustainable free deployment.

## Decision

Build one separate .NET 10 operator executable under `tools/`, without an API/Domain project reference, case storage, OIDC identity or case endpoints. Explicit private enablement, an independent 256-bit random bearer credential, independent certificate/environment/revision and an exclusive retained-key directory are required. Authentication precedes observations and body handling. No browser origin, cookie, forwarded identity, URL token, arbitrary configuration/file endpoint or automatic forwarding is supported.

Source-link the existing bounded encrypted-PFX loader and a small extracted framework retained-key-provider factory. The case API retains its previous application discriminator, purposes and descriptor-before-generation behavior. Only the probe additionally compares the bounded key-file count with the framework's parsed keys, refusing malformed/unreadable retained state before generation. This shares tested protection logic without introducing an application layer or deploying the case application as a diagnostic tool. [Framework protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

Separate explicit `init-keys` from `serve`. Initialization operates only on a prepared empty private directory; normal startup requires retained state. Both hold the same exclusive directory lease. The operator client uses fixed routes under one configured HTTPS origin, ordinary TLS validation, no redirects/cookies, bounded input/output and a network deadline. Reports always keep deployment readiness false. [The runbook](../usage/operator-review-probe.md) defines configuration, private evidence handling and limits.

## Consequences and evidence

The diagnostic host can observe original HTTP or a mismatched Host after operator authentication; it does not establish trusted forwarding or reviewer identity. These observations, including peer addresses and opaque receipts, remain private. The executable is a temporary assessment tool, not a production management interface. No new application ingress mode, provider migration, cloud resource, cost authorization or release automation is selected.

Disposable real-process tests cover explicit/no-overwrite initialization, encrypted key files, old-receipt retention over restart/new artifact path, wrong credential/certificate/corrupt/missing-state refusal and recovery, environment/revision isolation and competing writers. HTTP tests cover authentication/denial precedence and bounded raw observations; client tests cover HTTPS-only destinations, normal TLS rejection, redirects and bounded evidence. Existing API/browser tests continue to cover retained sessions after the factory extraction.

Local tests cannot establish free-tier eligibility, actual backend isolation, stable peer pinning, private cloud configuration delivery or retained storage across a real cloud release. Those remain [explicit acceptance gates](../tasks/managed-ingress-verification.md#evidence-and-decision-gates), followed by an owner decision for a concrete costed proposal before any resource creation.
