# Configure and verify a review tenant in Microsoft Entra ID

**Status: private local Entra role and revocation checks passed; hosted acceptance pending.** [ADR 0011](../adr/0011-use-entra-for-private-review-identity.md) selects Entra for the first private review registration. The [dated summary](#local-verification-on-2026-10-26) contains outcomes only; actual identifiers, grants/configuration files and credentials stay outside Git. Use an authorized tenant with a dedicated review registration and synthetic case content, never an employer/client tenant or records without authorization. Complete the remaining checks against the actual hosted environment before invitations.

## Prepare the registration and grants

1. An authorized app owner/tenant administrator registers a **single-tenant web application**, for users/guests in the intended workforce tenant. Record the tenant GUID/client ID privately. Register exactly the intended HTTPS `<PublicOrigin>/auth/callback` as a Web redirect, not SPA or wildcard. Use code flow; do not enable implicit access/ID token grants. Keep local review and eventual hosted registrations/configuration isolated as appropriate. [Microsoft registration guidance](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app).
2. Define enabled app roles with exact values `Reader` and `Caseworker`, assignable to Users/Groups. A Caseworker does not need both roles: the API includes read permission. Assign the intended regular test users to their roles through the enterprise application. Entra emits these application grants in the ID token's `roles` claims; this application does not call Graph or map email/domain/group names into permission. [Microsoft app-role guidance](https://learn.microsoft.com/en-us/entra/identity-platform/howto-add-app-roles-in-apps).
3. Set enterprise application **Assignment required? = Yes** and complete any required administrator consent under the tenant's policy. Use a regular non-administrator account for the unassigned-user test: assignment restrictions have administrator exceptions, while the application's missing-role 403 remains mandatory. Account/subscription/platform administration is not a Caseworker grant. [Microsoft assignment restrictions](https://learn.microsoft.com/en-us/entra/identity-platform/howto-restrict-your-app-to-a-set-of-users).
4. Provision the confidential-client credential privately with an owner/expiry/rotation plan. Supply its value only through the protected runtime configuration mechanism. The app requests only `openid`; no Graph API capability is needed by the implemented case workflow. Never put secrets/tokens into this document, public issues, screenshots or online token decoders.

Map the [existing application settings](authenticated-case-workspace.md#provider-and-runtime-configuration) as follows:

| Setting | Review tenant mapping |
| --- | --- |
| `Security:Authority` | `https://login.microsoftonline.com/<tenant-guid>/v2.0` |
| `Security:Issuer` | Exact issuer from that tenant's HTTPS discovery document; normally the tenant-specific v2 URL above. Verify the actual string, including any trailing slash. Do not use `common`, `organizations` or another tenant as a fallback. |
| `Security:ClientId`, `Security:ClientSecret` | This web registration's ID and externally supplied confidential credential |
| `Security:RoleClaim` | `roles` |
| `Security:ReaderRole`, `Security:CaseworkerRole` | `Reader`, `Caseworker` respectively, with exact case |
| `Security:PublicOrigin` | Browser-facing HTTPS origin; callback registration and tested proxy host must match exactly |
| Remaining session/store settings | Existing certificate-protected key directory, environment/revision and retained format-4 store; select DirectHttps or the explicit LoopbackProxy contract |

For a local private smoke test, use a browser that trusts the HTTPS server certificate and the app's existing direct-HTTPS setup. Do not disable metadata/backchannel TLS checks or add an impersonation endpoint/header. For proxy testing, complete the actual proxy installation/contract first. Do not change or initialize an existing case store to solve an identity configuration failure.

## Private smoke evidence

Run separate regular-user browser profiles/sessions. Preserve only synthetic data and non-sensitive outcome summaries in any public report. Keep provider sign-in traces, identity IDs, tenant/client values, full callback URLs/codes, cookies, CSRF values and credentials private. The automated fixture results do not populate this table as real-tenant evidence.

| Scenario | Expected result and observation to record privately |
| --- | --- |
| Anonymous entry/direct case request | Public shell offers sign-in; case API is no-store 401 problem, without redirect/case data. |
| Reader | Real code/callback sign-in succeeds; browse/search/details/history work; mutation controls are absent. A direct same-origin create/status attempt is 403 even with valid request-token proof. |
| Caseworker | Create one clearly synthetic case, progress New → InReview → Closed, inspect matching history. Confirm Secure/HttpOnly/host-only session cookie, CSRF-protected mutations and application-only sign-out. Do not record token/cookie values. |
| Unassigned regular account | Provider prevents sign-in under assignment policy, or a valid identity without mapped grants gets application 403. No case access or anonymous fallback. Never temporarily grant broad case access to make the test succeed. |
| CSRF/expiry | An otherwise authorized write or sign-out without valid proof returns 400 with `errors.csrf`. Expired application session yields 401; idle/pending views clear and no write replays automatically. |
| Grant change | Remove/change a test user's application assignment, then start a genuinely new app sign-in. Verify the provider's observed propagation and new role policy. An old application ticket may retain cached grants until its bounded expiry or explicit global revocation; local sign-out does not end the provider session. |
| Emergency revocation | Stop the sole app process, change `Security:SessionRevision`, restart with retained case data/session keys, verify an old ticket gets 401 and a new permitted sign-in succeeds. Case/history/creation-key state must survive. Follow the [existing runbook](authenticated-case-workspace.md#session-key-operations-and-emergency-revocation). |
| Misconfiguration/outage | Wrong tenant/client/credential, unavailable identity service or callback mismatch must fail closed with safe application errors; no anonymous permission, raw provider detail or case-store reset. Restore the private configuration and explicitly sign in again. |
| Real proxy path | Public host/HTTPS callback are correct, cookies/CSRF work, client-supplied forwarding chains cannot change the destination, and external connections cannot reach Kestrel's backend. Proxy/TLS renewal and safe diagnostics need their own evidence. |

Record the app commit/artifact, test date, pseudonymous actor labels, pass/fail, provider propagation observations and unresolved issues in an operator-controlled location. Retained-key replay/atomicity remains independently covered by the backend tests; keys do not become user ownership or authorization credentials. A tenant smoke pass alone does not complete the [hosting/recovery acceptance](../requirements/phase-8-secure-hosting.md).

## Local verification on 2026-10-26

Real Entra sign-in was exercised against commit `32b7e230ee8f1843ecc575976e442c246c5692de` using the release executable, direct loopback HTTPS with normal certificate validation, an isolated browser session and a separate synthetic format-4 store. One approved account was assigned roles sequentially on a dedicated single-tenant registration. No other reviewers were invited. Tenant/client/user IDs, credential values, provider traces and session material are deliberately excluded from this summary.

| Observed check | Result |
| --- | --- |
| Reader code/callback sign-in | Authenticated; read allowed, write flag false; case list returned **200** and direct create with request-token proof returned **403**. |
| Caseworker after application sign-out/new sign-in | Read/write flags true; synthetic creation **201**, New → InReview → Closed updates **200**, history contained three entries. |
| Retained creation-key replay | Same key/payload returned **201** with the original creation result after closure; one case and one binding remained. |
| Cookie properties | The application session cookie was Secure and HttpOnly; access bootstrap exposed flags and a bounded remaining lifetime. |
| Authenticated without a mapped role | After removing custom case roles and signing in again, authentication succeeded but read/write flags were false; direct case read/create both returned **403**. The existing default application-access assignment was preserved. |
| Assignment change | Existing application ticket retained its cached permissions; a fresh sign-in observed the newly assigned role or absence of mapped roles. Application sign-out allowed provider SSO at the next sign-in. |
| Emergency session revocation | Stopped the sole process, changed the session revision and restarted with the same store/keys. Old-ticket read/create returned **401**; a new Caseworker sign-in succeeded. |
| Retained-state comparison across restart | All case/history/creation-binding/migration rows and protected session-key files were unchanged: one case, three history entries and one creation binding. No initialization/upgrade/reset ran during restart. |

The account's Caseworker grant was restored after the negative check. Registration, runtime configuration, short-lived credential, protection material and outcome records remain in restricted operator-controlled storage outside the repository. No compute, disk or public hosting resources were provisioned for this check.

This does **not** verify a separate regular unassigned account, independent users/groups/guests, natural 30-minute expiry with the real provider, provider outage/misconfiguration, an actual proxy/public callback or five-client workload. Deterministic tests cover additional policy/CSRF/session failures, but are not substituted for these private/hosted checks. Hosted credential delivery/rotation, expiry/isolated-key drills, ingress/network fencing, diagnostics, readiness and release/restore acceptance remain pending. The complete scenario table above remains the operator checklist for those environments.
