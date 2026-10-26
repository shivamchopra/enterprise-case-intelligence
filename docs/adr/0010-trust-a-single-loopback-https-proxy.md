# 0010: Trust a single loopback HTTPS proxy

## Status

Accepted and implemented for local testing of the Phase 8 ingress boundary. Actual proxy/platform deployment and identity-tenant verification were pending at acceptance. [ADR 0011](0011-use-entra-for-private-review-identity.md) subsequently records private local Entra verification; actual proxy/platform deployment remains pending. This amends ADR 0009's direct-HTTPS-only transport boundary; its session, role, CSRF and retained-data decisions remain.

## Context

A TLS-terminating proxy forwards requests to Kestrel over HTTP. Without an explicit trust decision, the application either rejects that transport or risks accepting caller-supplied forwarding headers. The first review-hosting assessment recommends evaluating one Linux VM with a retained block disk, preserving the current single-process SQLite contract. That topology can use a same-host proxy; it needs no broad trusted network, multi-hop chain, host rewriting or client-IP forwarding.

## Decision

Keep `Security:Ingress=DirectHttps` as the Oidc default. Ignore forwarding headers there and retain the exact HTTPS public-origin check. Add explicit `LoopbackProxy` for Oidc only. Require one `--urls` HTTP binding with a literal loopback address and fixed nonzero port, no path/query/credentials or Kestrel endpoint overrides. The binding's address is also the sole trusted proxy peer. Do not infer ingress from environment names, request headers or cloud detection. LocalDemo keeps its existing explicit loopback restriction and cannot select proxy ingress.

Before processing forwarding information, check the physical peer and original HTTP scheme. Require exactly one `X-Forwarded-Proto: https` value and reject Forwarded/X-Forwarded-For/Host/Prefix headers or protocol chains. Configure the framework Forwarded Headers middleware for that protocol alone, one hop and one exact trusted address, clearing the default network/proxy lists. Normalize IPv4-mapped peers consistently. Never rewrite the host, path, client address or identity from forwarding headers. After forwarding, require the existing exact HTTPS public origin, then perform authentication → authorization → CSRF → case input/store work.

Reject framework-wide automatic forwarding configuration (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` / `ForwardedHeaders_Enabled=true`) at startup in every security mode. Its startup filter can execute before the application's trust checks and its options clear proxy restrictions. Offline lifecycle commands remain independent and do not start a request pipeline.

The selected proxy must terminate TLS, enforce the public host and overwrite the protocol header from its actual connection. It must remove the forbidden forwarding headers from client input and preserve the exact public Host, including a non-default port. Bind Kestrel only to loopback and prohibit external backend exposure. Same-host processes and the operator remain within this trust boundary; a loopback header is not a secret or a defense against an already compromised host. Multiple hops, remote ingress networks, ingress identity headers and forwarded path bases require a new decision and tests.

## Evidence and consequences

[Ingress tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/CaseIngressTests.cs) cover missing/chained/foreign forwarding, peer/protocol/host refusal, startup diagnostics before case I/O, authentication ordering, valid cookie/CSRF writes and retained replay. [Browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/AuthenticatedBrowserTests.cs) run Reader and Caseworker journeys through a real HTTPS-to-HTTP proxy fixture and the real OIDC handler. The fixture does not substitute for a deployed Nginx/service/network/certificate test or a private real-tenant smoke test.

This adds no database model, provider, migration, case field or actor history, and no automatic initialization or transaction replay. [The hosting assessment](../tasks/review-hosting-preparation.md) records alternatives and remaining release inputs. Availability, backup/restore, admission limits, readiness and restricted operational diagnostics remain separate Phase 8 work.

References: Microsoft's [proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0), [Nginx hosting guide](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0), and the framework's [automatic forwarding options](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/DefaultBuilder/src/ForwardedHeadersOptionsSetup.cs) and [startup filter](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/DefaultBuilder/src/ForwardedHeadersStartupFilter.cs).
