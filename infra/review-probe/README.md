# Undeployed F1 probe resource envelope

**Proposal only. Do not deploy this template yet.** The [separate probe application](../../docs/usage/operator-review-probe.md) is locally tested; Azure-side validation, private deployment tooling and exact eligibility remain pending. This envelope is not a deployment of the case API. It contains no database, identity registration, credential, key file, application package or deployment script.

The [2026-10-29 deployment-path assessment](../../docs/tasks/free-tier-deployment-preflight.md) identifies unresolved retained-storage permissions and first-initialization contracts. The [guarded preparation sequence](../../docs/usage/operator-review-probe.md#explicit-preparation-and-initialization) is now locally tested; the [preparation-only proposal](../../docs/tasks/preparation-only-deployment-proposal.md) now defines the resource/configuration/release/removal scope and fixes initial startup to `prepare`. Refreshed private reads still do not establish eligible allocation; exact private eligibility/policy evidence is required before any creation decision. Actual storage/isolation must pass before protection material or initialization is permitted.

The [verification contract](../../docs/tasks/managed-ingress-verification.md) defines the required evidence and owner decision before any resource creation. Use only a fresh, isolated disposable resource group and new resource names; never point this proposal at an existing application/plan. Region, names, subscription, quotas, inherited policy/add-on effects and free-tier eligibility must be verified privately. No parameter defaults or outputs publish those values.

| Proposed resource | Fixed boundary |
| --- | --- |
| One Linux App Service plan | Literal `F1` / `Free`, capacity 1, no per-site scaling; no paid-SKU parameter |
| One Linux code site | .NET 10, HTTPS-only, site/SCM minimum TLS 1.2, no Always On, FTP disabled, remote debugging/request diagnostics disabled; fixed independent-probe `prepare` startup |
| Two publishing policy children | SCM and FTP basic publishing credentials disabled; future deployment must use approved Entra/RBAC tooling |

The two initial non-secret app settings record storage intent and explicitly disable unrestricted automatic forwarding. Built-in code hosts ignore the storage flag; it is not proof of retained storage or access controls. They do not initialize a key directory or establish a trusted proxy. See the [storage assessment](../../docs/tasks/free-tier-deployment-preflight.md#blocker-1-retained-storage-and-permission-semantics). All credentials must be supplied later through a separately reviewed private process; do not add them as template parameters or outputs. No paid monitoring, custom domain/certificate, networking, storage account, SQL resource or Key Vault is included. Any added resource/feature requires a new review and cost decision.

The checker requires the literal startup `dotnet /home/site/wwwroot/EnterpriseCaseIntelligence.ReviewProbe.dll prepare`. It rejects an omitted/default command, normal serving, initialization, case-API startup and appended shell commands. There is no startup parameter or automatic bootstrap. Actual platform startup/port behavior remains unverified. A later accepted retention experiment needs a separate reviewed promotion update.

This is an initial-resource envelope, not a redeployment/rotation script. Reapplying its app-settings collection after private configuration could replace those values. Replacement probe binaries must use the reviewed artifact-release process while retaining configuration; later resource/configuration updates need an explicit safe merge/delivery design.

Run the structural check locally, without Azure calls:

```sh
dotnet run --file tools/ReviewHostPreflight.cs -- --proposal infra/review-probe/azuredeploy.json
```

Exit 0 means only that this constrained envelope passed the offline checks. The report always contains `deploymentReady: false`. It is not ARM/provider validation, an estimate for an existing resource group, a subscription eligibility check, a universal billing cap or permission to provision. CI only runs this offline check and disposable negative tests; it does not execute deployment commands.

Resource property references: [plan](https://learn.microsoft.com/en-us/azure/templates/microsoft.web/2024-11-01/serverfarms), [site](https://learn.microsoft.com/en-us/azure/templates/microsoft.web/2024-11-01/sites), [publishing policies](https://learn.microsoft.com/en-us/azure/templates/microsoft.web/2024-11-01/sites/basicpublishingcredentialspolicies). The fixed F1 choice follows the [free-host assessment](../../docs/tasks/zero-cost-review-hosting.md); product terms, actual eligibility and the complete proposal must be rechecked before any creation.
