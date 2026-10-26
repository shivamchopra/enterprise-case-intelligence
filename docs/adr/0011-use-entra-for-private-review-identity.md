# ADR 0011: Use Microsoft Entra ID for private review identity

## Status

Accepted for the first private local review registration. A real-provider check passed on 2026-10-26 against commit `32b7e230ee8f1843ecc575976e442c246c5692de`. Hosted registration, invitations and operational acceptance remain pending. This selects the provider anticipated by [ADR 0009](0009-authenticate-the-shared-case-workspace.md); it preserves that record's OIDC/session contract and [ADR 0010](0010-trust-a-single-loopback-https-proxy.md)'s ingress boundary.

## Context

The owner has authorized a dedicated review registration in an available Entra tenant, with account details and credentials kept outside the public repository. Disposable identity fixtures establish repeatable application behavior but cannot prove an actual provider registration, role issuance or code/callback exchange. The next useful step is private provider verification before selecting and funding hosting.

## Decision

Use a dedicated, owner-controlled **single-tenant Microsoft Entra web registration** in the authorized tenant for private local review. Pin the tenant-specific v2 authority and exact issuer; register the exact HTTPS callback. Use the existing confidential-client authorization-code/PKCE flow with implicit grants disabled. Keep local and eventual hosted registration/configuration isolated as appropriate.

Map only exact ID-token `roles` values `Reader` and `Caseworker`; a Caseworker includes read permission. Require enterprise-application assignment and manage individual application grants externally. Tenant/subscription administration and the default application-access assignment do not confer case permissions. The runtime requests only `openid` and needs no Graph API permission or account lookup.

Keep tenant/client/user IDs, credentials, callback traces, tokens, cookies and reviewer information out of Git, published assets and public evidence. Deliver the confidential credential through protected runtime configuration. Keep the case store and certificate-encrypted session-key directory outside release output, with restricted access and a separately protected key certificate. Record credential/certificate ownership, expiry and rotation privately. This local check used a short-lived client credential; it does not select the hosted secret-delivery mechanism.

Retain the 30-minute absolute application ticket and cached-grant behavior. A new sign-in observes changed provider grants; changing an assignment does not immediately revoke an existing ticket. Application sign-out does not end the provider session. Emergency revocation uses the documented stop → session-revision change → restart procedure against the same retained store/keys.

## Evidence and consequences

The [dated verification summary](../usage/entra-review-verification.md#local-verification-on-2026-10-26) records real Reader, Caseworker and unmapped-role outcomes, original-result replay and emergency revocation with retained state. One approved account was tested sequentially under different application assignments; this is not evidence for separate reviewer identities, a regular unassigned-user restriction or five-user concurrency.

No application code, database model, migration, provider, layer or production authentication shortcut is needed. CI continues to use disposable fixtures without real credentials. Entra selection does not select Azure compute, region, budget, a public hostname or a database replacement. Real proxy/network/TLS renewal, request limits, readiness, restricted diagnostics, release and backup/restore acceptance remain work in the [hosting preparation task](../tasks/review-hosting-preparation.md).
