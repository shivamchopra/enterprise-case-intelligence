# Complete the remaining private identity review

This task closes the three private-provider checks still open after the local Entra review: a separate regular account without an application grant, a real application ticket reaching its 30-minute absolute expiry, and a controlled identity-provider failure or configuration error. It is an owner-operated verification task, not a deployment task. Use synthetic case content, a dedicated review registration and a private evidence record. Do not publish tenant, client, account, credential, token, cookie, callback or provider-trace details.

The [Entra review guide](../usage/entra-review-verification.md) remains the source of truth for registration settings, role names, callback rules and the full scenario table. This task supplies the execution order and the minimum redacted evidence needed to decide whether the private checks are complete.

## Boundary and prerequisites

Complete these checks against the reviewed application commit or published local artifact. They do not require an Azure resource, a public hostname or a billing change. A direct HTTPS local host is sufficient for provider sign-in; the public callback, proxy and hosted acceptance remain separate gates.

Before starting, confirm privately that:

1. The single-tenant web registration has the exact HTTPS callback, `Reader` and `Caseworker` app roles, assignment required and a protected confidential credential. Keep the settings in the operator-controlled configuration store described in the [authenticated workspace guide](../usage/authenticated-case-workspace.md#provider-and-runtime-configuration).
2. Two ordinary test identities are available: one assigned review identity and one regular identity with no application assignment or case role. Do not use a tenant administrator as the unassigned-user substitute. If policy prevents a separate ordinary identity, record that prerequisite as blocked rather than temporarily granting broad access.
3. The run uses a disposable copy of the synthetic store and the existing protected session-key directory. Record the application commit, configuration revision and test date privately. Do not initialize, reset, rekey or alter a retained store to make a failed identity check pass.
4. The correct configuration can be restored without exposing its values. Take a private backup of the known-good settings and plan an explicit sign-in after restoration.

## Execute in order

### 1. Deny an unassigned regular identity

Use a fresh browser profile for the ordinary unassigned identity. Keep assignment required enabled and do not add a case role for the test. The provider may block the sign-in, or the application may authenticate the identity and return its normal authorization denial when no mapped role is present.

Pass only if the identity receives no case data and no mutation capability. A valid identity with no mapped role must receive the application’s 403 denial; a provider-blocked sign-in must not fall back to anonymous access. Record which of those two expected paths occurred and the observed propagation delay. Do not record the account name, object ID, token, cookie or full provider error.

### 2. Observe the real 30-minute ticket expiry

Sign in once with the assigned review identity and note the private start time. Do not refresh access, navigate through a new sign-in, use provider SSO to obtain a replacement ticket or change the session revision. Keep the browser and application process available until the application ticket is past its 30-minute absolute deadline.

At or after the deadline, request a protected read and, if useful, a no-op view refresh. The server must return its normal 401 authentication problem. The workspace must clear protected case/priority/draft state, stop pending work and avoid replaying a write. A new explicit sign-in may be performed only after the expired result is recorded. The browser countdown is advisory; the server response is the acceptance evidence.

### 3. Exercise a controlled provider failure or misconfiguration

Stop the private local host, preserve the synthetic store and session-key directory, then change one protected identity input in the private configuration, such as the issuer, authority, client ID or credential. Do not alter source files, public deployment settings or a shared tenant registration. Start the host and exercise the affected sign-in or metadata/callback path.

Pass only if the flow fails closed with a safe application/provider error, no anonymous permission, no raw provider detail and no store reset. Restore the known-good configuration, restart if required, and complete a fresh permitted sign-in. Verify that the original synthetic case, history, creation-key binding and protected key files remain intact. If the identity service is unavailable instead of the configuration being wrong, record that as a separate controlled outage result only when it can be performed without changing shared infrastructure.

## Redacted evidence record

Keep the detailed record in an owner-controlled location. The public repository may contain only a summary like this table with pseudonymous labels and no identifiers.

| Check | Safe fields to record | Completion condition |
| --- | --- | --- |
| Unassigned regular identity | Reviewed commit/artifact, pseudonymous actor label, date, `provider_blocked` or `app_denied`, propagation observation, pass/fail | No case read, write or anonymous fallback; no broad grant added for the test |
| Absolute expiry | Reviewed commit/artifact, private start/end observation, protected request result, workspace cleanup result, pass/fail | The real provider-backed application ticket is rejected after 30 minutes without an automatic write replay |
| Controlled provider failure | Reviewed commit/artifact, fault class (`issuer`, `authority`, `client`, `credential` or `outage`), safe error class, store/key integrity comparison, restoration result, pass/fail | Failure is fail-closed and non-destructive; known-good configuration and a fresh permitted sign-in recover the flow |

Do not include tenant/client/user IDs, credentials, access or ID tokens, cookies, CSRF values, callback codes, raw discovery or provider responses, full URLs with query values, case text or screenshots containing any of them. A private evidence reference or hash may be recorded outside Git when the owner needs auditability.

## Decision and follow-up

Mark this task complete only when all three rows pass and the private evidence record identifies the reviewed commit. If a prerequisite or provider policy prevents a row, leave it explicitly pending with the reason. Do not replace a real-provider result with a deterministic fixture result. The local tests still prove the application contract for absolute expiry, invalid OIDC responses, role denials, CSRF and retained-state recovery, but they cannot prove provider assignment propagation or an actual Entra callback.

After completion, update the private operator record and the roadmap status. Hosted callback, credential rotation, ingress/network fencing, diagnostics, readiness, five-client published-build workload and release/restore acceptance remain gated by the [hosted acceptance criteria](../requirements/phase-8-secure-hosting.md).

[ADR 0011](../adr/0011-use-entra-for-private-review-identity.md) · [Local identity verification](../usage/entra-review-verification.md) · [Deterministic security tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/CaseSecurityTests.cs) · [Authenticated browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/AuthenticatedBrowserTests.cs)
