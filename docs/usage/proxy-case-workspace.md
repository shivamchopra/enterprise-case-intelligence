# Run the authenticated workspace behind a same-host proxy

[ADR 0010](../adr/0010-trust-a-single-loopback-https-proxy.md) implements an optional ingress path for Oidc. Direct HTTPS remains the default. This is application/test support, not an installed proxy, cloud deployment or completed Phase 8 release. Use synthetic cases only.

## Configuration and trust

First supply the existing [Oidc configuration](authenticated-case-workspace.md#provider-and-runtime-configuration): exact public HTTPS origin, pinned issuer/client, trusted grant mapping, certificate-encrypted durable session keys and a supported retained case store. Then select:

```sh
# Non-secret transport settings; credentials must already be supplied privately.
export Security__Ingress=LoopbackProxy
dotnet run --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build -- --urls http://127.0.0.1:5080
```

Set `Security:PublicOrigin` to the proxy's external HTTPS origin, **not** the backend URL. The OIDC callback remains `<PublicOrigin>/auth/callback`. The proxy owns its TLS certificate; Kestrel needs no TLS server certificate on this HTTP backend. `Security:CertificatePath` still supplies the independent private certificate for session-key encryption/decryption.

The backend must be a single absolute `http://<literal-loopback-IP>:<nonzero-port>` URL without credentials, path, query or fragment. `localhost`, wildcard/non-loopback bindings, multiple URLs, HTTPS backend URLs, port zero and Kestrel endpoint overrides are refused. IPv6 loopback is supported with bracketed URL syntax. A proxy using IPv4 must connect over that IPv4 address; an unrelated loopback peer is not implicitly trusted.

Do not enable `ASPNETCORE_FORWARDEDHEADERS_ENABLED` or its `ForwardedHeaders_Enabled` configuration equivalent. Startup rejects unrestricted automatic forwarding in both Oidc and LocalDemo, including DirectHttps. Use the explicit setting above instead. Unset `Security:Ingress` to retain DirectHttps; LocalDemo rejects extra Security settings and never becomes a proxy/public mode.

## Proxy contract

The production proxy/server installation remains future work. Its configuration must satisfy all of the following before release:

- Accept only the intended public HTTPS host/port, with trusted, renewable TLS. Reject an unexpected Host before proxying. If HTTP is exposed for redirection/certificate renewal, it cannot pass requests to the application as HTTPS merely because a client sent a header.
- Connect to the configured HTTP loopback backend from the same address. Expose no external backend port through a listener, container publication, tunnel or ingress rule. Preserve the public Host exactly, including any non-default port.
- Replace any incoming `X-Forwarded-Proto` with one value derived from the proxy's real connection: `https`. Remove `Forwarded`, `X-Forwarded-For`, `X-Forwarded-Host` and `X-Forwarded-Prefix`; appending a chain is unsupported. Do not transform the path base or invent an identity header.
- Preserve methods, bodies, status codes, Location and all individual Set-Cookie headers. Do not follow backend redirects, share cookies between users, cache protected responses or automatically retry mutations. Apply the eventual finite body/admission/time limits and privacy-safe diagnostics at the proxy too.

The application verifies the physical peer and original HTTP scheme before calling the framework Forwarded Headers middleware. It consumes only the single HTTPS protocol value, then checks the public origin. Invalid ingress gets no-store `400 invalid_origin` with a generic about:blank problem and no Location, case/identity data or provider detail. A valid ingress still requires the same server policies and CSRF; it does not grant case access.

Host operators and same-host processes can reach loopback and belong to this trust boundary. OS identity/file permissions and the actual external network fence remain essential. This mode does not support a remote load balancer, App Service's platform proxy chain, multiple proxies or a shared-file SQLite deployment.

## Verification

[Ingress API/process tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/CaseIngressTests.cs) verify peer/header/protocol/host refusal, safe startup diagnostics, authentication before malformed input/I/O, valid writes, CSRF, roles and retained replay. Reader and Caseworker [real-browser journeys](../../tests/EnterpriseCaseIntelligence.Api.Tests/Security/AuthenticatedBrowserTests.cs) run through a test-only HTTPS proxy to a real HTTP Kestrel backend, including OIDC callback cookies and sign-out. The disposable provider/backchannel and generated certificates stay in tests; production still validates normal HTTPS identity metadata/backchannel traffic.

Before hosted access, verify the actual proxy implementation/configuration, network/backend exposure, TLS renewal, real tenant/grants, session revocation, operating controls and recovery. [The hosting preparation task](../tasks/review-hosting-preparation.md) identifies those inputs. No Nginx configuration, cloud resource or operating acceptance is certified by the test fixture.
