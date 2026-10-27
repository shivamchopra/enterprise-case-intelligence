# ADR 0012: Gate cloud hosting on zero recurring cost

## Status

Accepted on 2026-10-26 for deployment planning. Local development and private Entra verification remain available; cloud hosting is not provisioned. This amends the first-target recommendation in the [hosting assessment](../tasks/review-hosting-preparation.md), without changing ADRs 0007–0011 or their implemented persistence/security contracts.

## Context

This public reference project needs a sustainable review path without out-of-pocket recurring service charges. A temporary credit balance cannot establish that outcome. The earlier single-VM recommendation fits SQLite's current file/transaction model, but compute, disks, networking and recovery resources need their own funding and operating evidence.

## Decision

Keep the implemented workspace local while a complete zero-recurring-cost cloud configuration is assessed. Do not deploy the VM proposal against expiring credit merely to publish a demo. Do not upgrade a plan, remove spending protection, enable paid overage or add billable ancillary resources without a separate explicit, costed owner decision. A budget alert is an observation mechanism, not a hard spending limit.

Evaluate **App Service F1 with Azure SQL Database's free tier** as a possible low-traffic review path. This is a candidate for further design/testing, not a selected or supported deployment. Verify exact eligibility and configuration, including pausing SQL at its free limit, before proposing resource creation. The application must tolerate quota-driven downtime; no continuous availability or full Phase 8 acceptance follows from a free SKU.

The current SQLite file must not be placed on App Service's shared or temporary filesystem. A selected SQL backend requires real-provider EF Core Code First models/migrations, explicit lifecycle/adoption and retained-history/key-replay tests. The platform's HTTPS ingress, host/forwarding trust, secret delivery, durable protected session keys and recovery behavior also require their own design and evidence; the same-host loopback proxy contract is not automatically portable.

## Consequences

No runtime/schema change, Azure compute/storage resource, subscription change or paid service is introduced by this decision. Keep personal account, billing and benefit details outside Git. The [dated assessment](../tasks/zero-cost-review-hosting.md) lists verified public product constraints and the next delivery gates. Region/currency selection remains deferred until a compatible free configuration is proven. Existing local tests and private Entra evidence remain useful regardless of the eventual host.
