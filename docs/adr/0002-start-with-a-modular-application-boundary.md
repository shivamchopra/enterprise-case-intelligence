# 0002: Start with a modular application boundary

## Status

Accepted.

## Context

Phase 0 has two simple endpoints and no business workflows or infrastructure. Independent services would add deployment, networking, testing and operational costs without evidence of a requirement.

## Decision

Begin with one deliberately small deployable ASP.NET Core application and an inward Domain project boundary. Api references Domain; tests reference Api. Domain remains empty until actual business behavior is defined. Do not scaffold additional layers or services.

## Rationale

A small application supports fast feedback, comprehensible dependencies and disciplined scope. Independent services should emerge only when requirements justify deployment independence, scaling differences, reliability, team ownership, security boundaries, data ownership, operational isolation or distinct technology requirements.

## Consequences

There is one application to build, test and eventually deploy. Internal boundaries may evolve as real behavior appears. Service extraction would require explicit contracts, ownership and operating costs, documented in future ADRs. This decision neither declares a microservices architecture nor commits the final system to a modular monolith.

## Alternatives Considered

- Microservices from day one: unjustified operational and coordination complexity.
- One project containing everything: simple initially, but omits the minimal inward domain boundary this project intentionally establishes.
- A full layered skeleton: adds empty projects and speculative abstractions before any need exists.
