# 0009: Authenticate the shared case workspace

## Status

Accepted for the locally testable identity/session slice of Phase 8. Hosted release, provider provisioning and operational acceptance were separate work. [ADR 0010](0010-trust-a-single-loopback-https-proxy.md) subsequently amends the transport boundary with explicit same-host proxy support. [ADR 0011](0011-use-entra-for-private-review-identity.md) selects Entra and records private local provider verification; hosted provisioning and acceptance remain pending.

## Decision

Use ASP.NET Core's OpenID Connect and cookie handlers with a configured HTTPS authority, exact issuer, client and exact trusted role values. Use confidential authorization-code flow, PKCE and form-post callback, retaining framework state/nonce/correlation validation. Project only a verified subject, issuer, recognized Reader/Caseworker roles and a session revision into the encrypted application cookie. Do not create an application user/password database, mint bearer tokens, trust identity headers or add a production impersonation route.

Require explicit `Security:Mode=Oidc` or `LocalDemo` for normal hosting. LocalDemo requires explicit loopback URLs, no Kestrel endpoint overrides or OIDC settings, and rejects non-loopback requests; it is a deliberate local exception. Offline lifecycle commands remain independent of sign-in configuration. Oidc requires an exact HTTPS public origin, protected existing key directory, certificate/private key, environment discriminator and session revision. Direct HTTPS is supported in this slice; proxy/TLS termination remains an unimplemented hosting decision. Untrusted forwarded headers cannot select the public origin or bypass security.

Persist Data Protection keys separately from the case store, encrypt them with an operator-supplied certificate and isolate their purpose by environment. Use a host-only Secure/HttpOnly/Lax application cookie with 30-minute absolute expiry and no sliding renewal. A changed session revision invalidates previously issued tickets after an operator-controlled restart, without resetting case data. This does not provide immediate upstream account/role revocation or end every identity-provider/browser session.

Require server-side read/write policies on every case route. Caseworker includes Reader; missing/unknown grants add no permission. Case API denials are exact generic non-redirecting 401/403 problems. Validate antiforgery proof on otherwise authorized JSON writes and local sign-out before parsing case input or touching storage. Read session/bootstrap state through a same-origin endpoint, carrying same-origin credentials and a request token only for eligible users. Keep authentication, authorization, CSRF, input and storage failure precedence explicit.

The UI is a public embedded shell with no case/identity data embedded. It bootstraps before fetching cases, hides mutations for Readers, and clears/invalidates protected views on denial/sign-out. Responses from a prior session cannot repopulate them. It never automatically retries a write after sign-in, and preserves frozen creation context only while the page stays alive. Full navigation loses that context. Creation keys remain store-wide in this shared workspace, with authorization/CSRF applied before replay lookup.

## Verification and boundaries

Test framework cookie/CSRF/policy behavior independently of the UI; exercise the real OIDC handler with a disposable test provider/backchannel and generated signing keys. Browser tests use real HTTPS hosting and test-only identity configuration. No real tenant secret, certificate, authentication shortcut or disabled TLS validation is added to production. Private provider/tenant smoke tests and grant propagation remain required before invited hosting.

Keep the existing local workflow, Domain independence, SQLite format 4, migrations and atomic case/history/key semantics. New authentication packages stay in Api. Test-only hosts may supply controlled identity/signing/time fixtures; production accepts only its configured validated authority. The external tenant, Azure service, ingress, storage topology, backup/restore, readiness and restricted operational logs are not established by this decision.

References: [OIDC web authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0), [cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0), [antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), [Data Protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0) and [Phase 8 requirements](../requirements/phase-8-secure-hosting.md).
