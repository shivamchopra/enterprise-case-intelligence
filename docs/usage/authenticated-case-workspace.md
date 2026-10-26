# Authenticated shared case workspace

This is the locally testable identity/session slice of Phase 8, implemented by [ADR 0009](../adr/0009-authenticate-the-shared-case-workspace.md). It is not a hosted release. A real review tenant, invitations/grants, cloud platform/storage, ingress, restricted diagnostics, readiness and backup/restore acceptance remain pending. All cases must be synthetic.

## Explicit modes

Normal HTTP startup requires `Security:Mode`:

- **LocalDemo:** explicitly configured loopback URLs only; no other `Security` settings or Kestrel endpoint overrides. Requests need both a loopback peer and loopback Host, including protection against non-loopback Host rebinding. It ignores identity headers/cookies and does not sign users in. Use this standalone local exception for the [curl/browser walkthrough](local-case-workflow.md), never behind a proxy or public ingress. Environment names such as Development do not select this mode.
- **Oidc:** configured HTTPS OIDC authority, exact issuer, confidential client, trusted role mapping and protected session keys. Direct HTTPS only in this slice. Requests must use the exact configured host/port; HTTP and unexpected hosts fail with generic `400 invalid_origin`. Forwarding headers do not alter that decision. TLS termination/proxy support requires a later hosting decision.

Missing/invalid security configuration fails before case storage validation, with a concise diagnostic. Offline `init-store`, `inspect-store` and `upgrade-store` do not require identity configuration and never start HTTP. Initializing a case file remains a separate explicit operation; skip initialization when resuming retained data.

## Provider and runtime configuration

Register a confidential web application at a maintained OIDC provider. Enable authorization-code flow with PKCE and form-post response mode; register **exactly** `<PublicOrigin>/auth/callback`. Supply the provider's HTTPS authority and exact ID-token issuer, audience/client ID and explicit trusted grant claim/values. The provider must issue an immutable `sub` and the configured roles in the validated ID token. Invite only the intended synthetic-workspace reviewers. Entra ID remains a candidate, not a provisioned/verified tenant. Do not use a common/multi-tenant issuer or copy fixture identities into runtime configuration.

Configuration uses ASP.NET Core providers; environment settings replace `:` with `__`. Supply credentials through a protected local environment or secret provider, not command arguments, checked-in appsettings, screenshots or shell history.

| Setting | Required Oidc value |
| --- | --- |
| `Security:Mode` | `Oidc` |
| `Security:Authority` | Maintained HTTPS authority for discovery/signing keys; no query, fragment or user information |
| `Security:Issuer` | Exact expected issuer string, including any trailing slash; issuer and client/audience are checked |
| `Security:ClientId`, `Security:ClientSecret` | Registered confidential client and externally supplied secret |
| `Security:RoleClaim` | Trusted ID-token grant claim, e.g. a tenant's application `roles`; not an identity/internal claim |
| `Security:ReaderRole`, `Security:CaseworkerRole` | Distinct exact external grant values mapped to Reader and Caseworker |
| `Security:PublicOrigin` | Exact HTTPS origin including non-default port, without path/query/fragment |
| `Security:EnvironmentId` | Stable unique environment discriminator, up to 80 characters |
| `Security:SessionRevision` | Operator-controlled revocation revision, up to 80 characters |
| `Security:KeyDirectory` | Existing absolute directory outside application publication output, durable across restart, accessible only to the runtime/operator |
| `Security:CertificatePath` | Absolute path to a valid RSA PFX with private key for Data Protection encryption/decryption; keep separate from case files and publication output |
| `Security:CertificatePassword` | PFX password when needed; supply externally |
| `Kestrel:Certificates:Default:Path`, `...:Password` | Externally supplied HTTPS server certificate configuration for direct Kestrel hosting; independent of session-key protection |
| `CaseStorage:DatabasePath` | Existing supported local format-4 store; one process per file |

With those settings supplied privately and the retained store already initialized, start using the configured HTTPS origin:

```sh
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- --urls "$Security__PublicOrigin"
```

Use a browser that trusts the server certificate. Production performs normal HTTPS metadata/backchannel validation; there is no certificate-validation bypass or test-login setting. Use a new shell/configuration when switching to LocalDemo, rather than leaving Oidc credentials/grants configured. This document establishes no safe cloud filesystem, public ingress or deployment principal.

## Browser and API behavior

The embedded `/workspace` shell/assets contain no case or identity values. `GET /auth/session` returns only access flags, the remaining application-session lifetime and an antiforgery request token for authenticated sessions; it sets no-store and carries no subject, name, role claim or identity-provider token. The browser loads cases only after bootstrap. It shows a Reader view or Caseworker mutation controls, with all case permissions also enforced by named server policies.

| Actor | Browse/search/details/history | Create/status, including replay/no-op |
| --- | --- | --- |
| Anonymous, invalid or expired session | `401 authentication_required` | `401 authentication_required` |
| Valid identity without mapped grants | `403 access_denied` | `403 access_denied` |
| Reader | Allowed | `403 access_denied` |
| Caseworker | Allowed | Allowed with valid CSRF proof |

Denials are no-store `application/problem+json` with `type: about:blank`, status/title/code only, no Location, HTML or raw provider detail. Precedence is authentication → authorization → antiforgery → existing input/domain validation → storage. Existing authorized input, transition, provenance and provider-failure contracts remain. Authorization applies before retained-key lookup; another Caseworker in this shared workspace can replay the same key/payload, while a Reader cannot. Keys stay global to the intact store, with no actor/tenant partition or new history identity.

`GET /auth/login` starts the framework's code/PKCE/state/nonce/correlation flow. Query arguments are rejected; the sole completion destination is local `/workspace`. The server retains only projected verified identity/permissions/revision in its encrypted cookie, never access/refresh/ID tokens. The host-only `__Host-eci-session` cookie is Secure/HttpOnly/SameSite=Lax, nonpersistent, with 30-minute absolute expiry and no sliding renewal. Closing a browser is not a guaranteed session-revocation mechanism.

For authenticated writes, obtain the same-origin request token from `/auth/session`, keep its antiforgery cookie, then send `X-ECI-CSRF` with JSON mutations or `POST /auth/logout`. Proof is required even for creation replay and same-status success. Missing/invalid/foreign-user proof returns `400 validation_failed` with `errors.csrf`, before input/store access. Tokens are not authorization grants. CORS is not enabled. The browser sends same-origin credentials and keeps tokens/drafts in memory, not web storage or URLs.

Sign-out requires CSRF and ends the application session, without claiming to end the provider session or revoke every copied cookie. Unmapped identities can still sign out. A Reader/Caseworker grant change is observed at a new sign-in; existing cookies are not immediately revalidated against the provider. There is no per-user session database or immediate upstream revocation claim.

At the supplied absolute session deadline, or on a 401/403 or sign-out, the browser clears case/draft fields, hides protected views and invalidates outstanding responses. The browser deadline is best effort under browser suspension; the server enforces expiry on every request. It does not retry mutations automatically. An uncertain creation's frozen key/payload may remain only in the current page's memory, for explicit reconciliation after access returns. Full sign-in navigation/reload loses that context; warnings and best-effort unload prompts explain this. Reconcile saved cases before submitting a new key after losing context. Refresh access is an explicit session check, never a write replay.

## Session-key operations and emergency revocation

Keep the key directory, its environment discriminator and protection certificate/private key durable and permission-restricted across supported restarts. Startup exercises protection/unprotection before touching the case store. XML keys on disk are certificate-encrypted; case data is not thereby encrypted. Other processes with the key directory and private key can forge sessions, so both are secrets. Do not share them between environments or put them in a case-data backup/public build. Auth protocol/framework request logging is suppressed in the shipped defaults to avoid callback/token/query leakage; restricted security-event diagnostics and ingress retention remain future hosting work.

For an emergency application-wide session revocation, stop the sole API process, change `Security:SessionRevision`, restart with the same valid case store, and verify an old session now gets 401 while a new authorized sign-in works. Do not initialize, reset or rekey case/history/creation data. This invalidates cookies only on instances using the new revision; complete the operator-controlled restart before resuming access. Provider account/grant revocation and incident response remain separate operator actions.

If replacing/losing/compromising the protection certificate or session keys, stop hosting, provision a new valid protection certificate and new protected empty key directory, change the session revision and restart against the **same case store**. All application sessions must sign in again. Protect/archive the old key material according to the eventual incident/retention policy; do not delete case storage. Seamless certificate rollover with retained sessions is not implemented. Plan replacement before the protection certificate expires; startup refuses an invalid certificate. No automatic key-reset fallback exists.

## Evidence and remaining acceptance

[Security API tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/CaseSecurityTests.cs) cover policy/CSRF ordering, direct calls, global retained-key replay, exact denials, absolute expiry, encrypted key persistence/restart, revision/environment isolation and invalid OIDC callback/token cases. [Authenticated browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/AuthenticatedBrowserTests.cs) exercise the real OIDC handler through a disposable provider and real HTTPS app host, Reader/Caseworker flows, sign-out and session loss during pending reads/writes. Generated certificates, static test metadata/backchannels and controlled clocks exist only in test code; no real tenant secret is needed by CI.

The existing local browser tests continue against the published executable in CI. Authenticated browser tests use the built app through a test-hosted HTTPS server to inject only test identity dependencies; they do not establish the production provider, ingress or cloud deployment. Complete a private real-provider smoke test, role-change/revocation drill, platform/storage ADR, five-client workload, operating/recovery controls and the remaining [SH-01–SH-14 acceptance conditions](../requirements/phase-8-secure-hosting.md#threats-and-acceptance) before inviting hosted users.
