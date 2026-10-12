# 0001: Use .NET 10 and ASP.NET Core

## Status

Accepted.

## Context

The project needs a supported enterprise engineering foundation with mature HTTP hosting, automated testing and tooling. Its technical direction begins with strong C# / .NET expertise and may later include cloud integration and specialized AI components.

## Decision

Use .NET 10, ASP.NET Core and the normal C# language version supplied by the SDK. Pin a stable installed SDK baseline in `global.json`, allow stable feature-band roll-forward within .NET 10.0 and exclude prereleases. Use CLI-created SLNX and the standard xUnit/VSTest template approach with ASP.NET Core integration testing.

## Rationale

.NET 10 is the current LTS foundation with enterprise relevance and mature supported tooling. It fits the project's direction while preserving and demonstrating strong .NET expertise. Minimal APIs keep this phase's small HTTP surface understandable.

## Consequences

Contributors need a compatible .NET 10 SDK. Dependency updates must remain compatible with net10.0. Supported runtime/SDK patches should be kept current. This decision does not prevent future Python or other specialized components when requirements justify them.

## Alternatives Considered

- An older supported .NET release: less aligned with the chosen current LTS baseline.
- A preview .NET release: inappropriate for this stable foundation.
- A different primary stack: possible for specialized future components, but unnecessary for the initial .NET engineering objective.
