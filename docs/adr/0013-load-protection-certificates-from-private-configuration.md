# ADR 0013: Load protection certificates from private configuration

## Status

Accepted and implemented for local verification. This amends [ADR 0009](0009-authenticate-the-shared-case-workspace.md)'s protection-credential configuration; issuer/role/session/CSRF and ingress contracts remain. [ADR 0012](0012-gate-cloud-hosting-on-zero-recurring-cost.md)'s cost gate remains in force. Actual App Service credential delivery/retention and hosted acceptance are unverified.

## Context and decision

The [managed-host assessment](../tasks/app-service-compatibility.md) identified protected configuration as a possible credential-delivery path without assuming a paid platform certificate-upload feature. Add this capability independently of a host, without a database change or cloud resource.

Preserve the existing `File` source as the default. Add explicit `Security:CertificateSource=Configuration`, with an encrypted, password-authenticated Base64 PFX and a required password supplied privately. Reject missing/conflicting sources with no fallback. Both sources require a currently valid RSA private key. The [runbook](../usage/authenticated-case-workspace.md#protection-certificate-sources) defines the settings and bounded input contract.

Use .NET's `X509CertificateLoader` with its default KDF/MAC work limits, at most one key and ten certificates for configuration import. Use `System.Security.Cryptography.Pkcs` to verify password integrity/MAC and require one encrypted PKCS#8 key bag with ordinary certificate bags. Reject unencrypted keys, nested/opaque bags and trailing bytes. Inspect only after the bounded framework import; implement no ASN.1 parser or cryptographic algorithm. Clear the owned decoded buffer on read/import completion and dispose failed native key imports. Provider strings/environment values remain in process memory and cannot be promised erased.

Before normal Data Protection key generation, construct the framework `XmlKeyManager` and resolve every retained key descriptor. A fresh protect/unprotect probe alone can otherwise generate a new default key when an old descriptor cannot be decrypted. An incompatible replacement must fail before adding keys or touching the store. Preserve normal generation for a valid new/existing environment; add no reset or custom key manager.

## Consequences and evidence

Switching delivery sources with the **same certificate/private key**, retained key directory, environment and revision preserves sessions/data. A different certificate is not seamless rollover: follow the explicit replacement/revocation runbook with a new protected key directory and revision, retaining the case store. Changes take effect at restart.

The source name does not secure a configuration provider. Keep PFX/password values out of Git, checked-in settings, command arguments, release output and logs; use restricted runtime configuration. This credential remains independent of HTTPS server certificates and case-creation keys.

[Certificate tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/ProtectionCertificateTests.cs) cover malformed/password/certificate failures, safe process diagnostics, no PFX file, encrypted retained keys/cases/history/replay across source changes, revision revocation and incompatible replacement refusal. [Browser tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/AuthenticatedBrowserTests.cs) run Reader/Caseworker sign-in, CSRF, writes and sign-out through direct HTTPS and loopback proxy with both sources. All material is disposable; no real identity secret is needed by CI.

No database format/model, EF migration, provider, public endpoint or Azure resource changes. Hosted configuration permissions/rotation, ingress, SQL compatibility and operational recovery remain separate evidence.
