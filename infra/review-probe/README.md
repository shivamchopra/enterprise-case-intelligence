# Undeployed F1 probe resource envelope

**Proposal only. Do not deploy this template yet.** The separate probe application and Azure-side validation are not implemented/performed. This envelope is not a deployment of the case API. It contains no database, identity registration, credential, key file, application package or deployment script.

The [verification contract](../../docs/tasks/managed-ingress-verification.md) defines the required evidence and owner decision before any resource creation. Use only a fresh, isolated disposable resource group and new resource names; never point this proposal at an existing application/plan. Region, names, subscription, quotas, inherited policy/add-on effects and free-tier eligibility must be verified privately. No parameter defaults or outputs publish those values.

| Proposed resource | Fixed boundary |
| --- | --- |
| One Linux App Service plan | Literal `F1` / `Free`, capacity 1, no per-site scaling; no paid-SKU parameter |
| One Linux code site | .NET 10, HTTPS-only, site/SCM minimum TLS 1.2, no Always On, FTP disabled, remote debugging/request diagnostics disabled |
| Two publishing policy children | SCM and FTP basic publishing credentials disabled; future deployment must use approved Entra/RBAC tooling |

The two initial non-secret app settings enable App Service storage and explicitly disable unrestricted automatic forwarding. They do not initialize a key directory or establish a trusted proxy. All credentials must be supplied later through a separately reviewed private process; do not add them as template parameters or outputs. No paid monitoring, custom domain/certificate, networking, storage account, SQL resource or Key Vault is included. Any added resource/feature requires a new review and cost decision.

This is an initial-resource envelope, not a redeployment/rotation script. Reapplying its app-settings collection after private configuration could replace those values. Replacement probe binaries must use the reviewed artifact-release process while retaining configuration; later resource/configuration updates need an explicit safe merge/delivery design.

Run the structural check locally, without Azure calls:

```sh
dotnet run --file tools/ReviewHostPreflight.cs -- --proposal infra/review-probe/azuredeploy.json
```

Exit 0 means only that this constrained envelope passed the offline checks. The report always contains `deploymentReady: false`. It is not ARM/provider validation, an estimate for an existing resource group, a subscription eligibility check, a universal billing cap or permission to provision. CI only runs this offline check and disposable negative tests; it does not execute deployment commands.

Resource property references: [plan](https://learn.microsoft.com/en-us/azure/templates/microsoft.web/2024-11-01/serverfarms), [site](https://learn.microsoft.com/en-us/azure/templates/microsoft.web/2024-11-01/sites), [publishing policies](https://learn.microsoft.com/en-us/azure/templates/microsoft.web/2024-11-01/sites/basicpublishingcredentialspolicies). The fixed F1 choice follows the [free-host assessment](../../docs/tasks/zero-cost-review-hosting.md); product terms, actual eligibility and the complete proposal must be rechecked before any creation.
