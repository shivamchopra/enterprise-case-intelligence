# App Service compatibility and read-only preflight

**Status: compatibility assessment/catalog tool and configuration-backed certificates implemented locally; App Service deployment blocked.** This is the next planning/tooling slice under [ADR 0012](../adr/0012-gate-cloud-hosting-on-zero-recurring-cost.md), not a provider/platform selection or cloud release. The catalog tool performs only reads; the certificate implementation/tests require no Azure operations. Actual account/billing/eligibility details stay private.

## Findings on 2026-10-27

| Boundary | Current implementation and platform finding | Required evidence before deployment |
| --- | --- | --- |
| Runtime and free plan | The standalone tool advertised Linux .NET 10 and F1 in both East US and Canada Central on 2026-10-27; both reports kept deployment readiness false. | A catalog entry does not establish subscription quota, allocation, SQL free-tier eligibility or complete-stack cost. |
| HTTPS ingress | App Service terminates TLS upstream; the documented app-facing path is HTTP with forwarded request information. Current DirectHttps requires HTTPS at the app, while LoopbackProxy permits only one exact loopback peer and a restricted single header. | A separate explicit managed-ingress trust design, exact public Host/callback and external HTTPS-only/backend fencing evidence. Do not enable unrestricted automatic forwarding or treat arbitrary private networks/website headers as identity. |
| Case persistence | The current format-4 SQLite file requires an ordinary retained local filesystem and one process. Shared app content and temporary container storage do not establish this contract. | A justified server-provider ADR, real-provider EF Code First migrations and explicit lifecycle/adoption/retained-key tests. Do not put the SQLite file in `/home` to bypass this work. |
| Session keys | The app explicitly encrypts persistent Data Protection keys with a private RSA certificate. App Service's default shared key directory does not encrypt those keys at rest. | Preserve explicit encrypted key persistence, environment/revision isolation and startup protection/unprotection checks across restart. Do not switch to default or ephemeral keys merely to launch. |
| Protection credential | [ADR 0013](../adr/0013-load-protection-certificates-from-private-configuration.md) now supports File or explicitly selected, bounded password-authenticated PFX configuration. The documented platform certificate-upload workflow lists Basic or higher tiers; public TLS remains separate. | The application capability is locally verified. Still prove actual free, restricted credential delivery/rotation and durable encrypted keys across App Service restart/replacement without published secrets or paid services. |
| Availability and recovery | F1 quota responses can occur before the API; SQL AutoPause can make the store unavailable. Free-tier restore constraints differ from the local-file workflow. | Test quota/pause failures, no automatic write replay, replacement/reconciliation and explicit operating-target amendments if needed. No full SH-09–SH-13 acceptance is established. |

Platform findings: [App Service HTTPS detection](https://learn.microsoft.com/en-us/azure/app-service/configure-language-dotnetcore#detect-https-session), [persistent/container storage](https://learn.microsoft.com/en-us/azure/app-service/configure-custom-container#use-persistent-shared-storage), [default Data Protection keys](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0), [certificate prerequisites](https://learn.microsoft.com/en-us/azure/app-service/configure-ssl-certificate). The incompatibility conclusions are this project's assessment against its implemented contracts. The [cost assessment](zero-cost-review-hosting.md) records the product quotas and SQL recovery limitations separately.

## Run the catalog checker

Use the repository's .NET 10 SDK and an already authenticated Azure CLI on macOS/Linux. From the repository root:

```sh
dotnet run --file tools/ReviewHostPreflight.cs -- --region eastus
# Or check another canonical lowercase Azure region code, for example canadacentral.
```

This optional [file-based operator tool](../../tools/ReviewHostPreflight.cs) has no additional application package references and is not part of the API deployment. Its complete Azure command allowlist is:

```sh
az webapp list-runtimes --os linux --only-show-errors --output json
az appservice list-locations --sku F1 --linux-workers-enabled --only-show-errors --output json
```

It does not sign in, switch subscriptions, read app settings/secrets, register providers, query balances, deploy, create/update/delete resources or change spending protection. Normal local SDK/CLI caches may be used. The region is validated before any CLI call and is only a local result filter. Each CLI process has a 30-second limit and bounded captured output; raw output/errors are never printed. A missing CLI, failed command or malformed/oversized response produces `unavailable` rather than a successful check.

The JSON report includes only the region, catalog statuses, fixed unresolved gates and **`deploymentReady: false`**. It never infers eligibility from a catalog or turns a green result into deployment approval. Preserve actual private evidence separately, and never replace authentication/ingress checks with this operator report.

| Exit code | Meaning |
| --- | --- |
| `0` | Both catalogs advertised the requested candidates. Deployment remains blocked. |
| `1` | A candidate was not advertised or a catalog was unavailable; investigate privately. |
| `2` | Invalid arguments; no Azure commands ran. |

[Preflight tests](../../tests/EnterpriseCaseIntelligence.Api.Tests/Hosting/ReviewHostPreflightTests.cs) cover CLI catalog formats, exact version/region matching, failed/malformed/oversized results, the read-only command allowlist, input validation, privacy and the permanently false deployment-readiness flag. CI injects disposable catalog responses and needs no Azure CLI login or real account. The linked source is compiled/tested by the existing test project; the standalone file-based entry point is also verified locally.

## Implemented locally: protection credential from protected configuration

App Service app settings are injected into the process and encrypted at rest by the platform. [ADR 0013](../adr/0013-load-protection-certificates-from-private-configuration.md) implements this application input path with an independent encrypted PFX. This does not by itself prove secure provisioning, access controls, rotation or F1 compatibility. [App setting behavior](https://learn.microsoft.com/en-us/azure/app-service/configure-common#configure-app-settings).

The [source-switch runbook](../usage/authenticated-case-workspace.md#protection-certificate-sources) defines the exact settings, input limits, ownership and failure behavior. The default file setup remains; configuration mode is explicit and cannot fall back. Tests cover invalid envelopes/passwords/RSA validity, safe environment-supplied process diagnostics, no PFX file, encrypted keys/cases/history/replay across source switches, session revision revocation, and refusal of a different valid certificate over retained keys. Reader/Caseworker browser journeys run with both sources through direct HTTPS and the loopback proxy. No real identity credential or cloud resource is involved.

## Next delivery gates

1. The [managed-ingress verification contract](managed-ingress-verification.md) now defines exact-origin/peer/header requirements, private credential delivery and old-receipt retention evidence. Its [undeployed F1 envelope](../../infra/review-probe/README.md) has an offline constraint check, not platform validation. The [independent operator probe](../usage/operator-review-probe.md) now has local process/client access and retained-receipt evidence. Next verify a concrete free-tier deployment path and exact eligibility privately before any creation review. Keep current direct/loopback modes strict; no unrestricted forwarding or header-based identity.
2. Verify actual F1 credential/configuration permissions, delivery/rotation and protected-key retention privately, using a complete zero-overage resource proposal before any creation. Catalog success and local certificate tests do not supply that evidence.
3. Select and implement an EF Code First server provider only when its platform/cost fit is established; preserve explicit lifecycle/adoption, bounded queries and retained history/key replay.
4. Complete quota/pause/replacement, release, restricted diagnostics, workload and recovery acceptance. If the free topology cannot meet the operating targets, revise them explicitly or remain local.

App Service and SQL are still assessment candidates, not a supported deployed stack. The contract/retention plan and resource envelope are prepared. The independent operator probe is now locally tested; the next slice is private free-tier deployment-path/eligibility verification. Actual Azure validation, owner approval and platform evidence remain pending. No cloud resources are created or paid certificate/Key Vault/compute services assumed by the certificate implementation.
