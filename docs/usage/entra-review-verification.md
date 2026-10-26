# Configure and verify a review tenant in Microsoft Entra ID

**Status: operator procedure; no actual tenant, registration, grants or smoke result is supplied by this repository.** Application OIDC/policy/session and [proxy ingress](proxy-case-workspace.md) have local automated tests. A private real-provider check is still required before invited hosting. Use an authorized dedicated review tenant and synthetic case content, never an employer/client tenant or records without authorization.

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
