# Free-tier deployment-path assessment

**Assessed on 2026-10-29. Result: do not provision the proposed probe yet.** The independent probe is locally tested, but the deployment path has unresolved key-storage and first-initialization contracts. Subscription-specific eligibility is not established. This records the next gate under [ADR 0012](../adr/0012-gate-cloud-hosting-on-zero-recurring-cost.md), not a platform selection or authorization to create resources.

## What was verified without provisioning

The installed Azure CLI was `2.91.0`. Fresh catalog reads advertised Linux .NET 10 and F1 Linux in East US and Canada Central. These observations do not reserve capacity or establish eligibility. Microsoft's Linux pricing page lists F1 shared compute at $0 with 60 CPU minutes/day, 1 GB memory and 1 GB storage, for experimentation without an SLA. The proposed envelope still includes only that plan, one code site and its two basic-publishing policies. SQL, paid diagnostics, external storage, Key Vault, domains and certificates are not included. [Linux pricing](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/), [undeployed envelope](../../infra/review-probe/README.md).

Private reads covered subscription metadata, the Web provider, regional usage and listed permission patterns. They did **not** establish usable F1 allocation. The regional quota requests returned unavailable evidence, not a proven zero limit or approval. Missing/unavailable data must remain unknown. The [usage API](https://learn.microsoft.com/en-us/rest/api/appservice/get-usages-in-location/list?view=rest-appservice-2024-11-01) reports regional core usage; even a successful generic response would need an applicable F1-specific interpretation.

Account/provider/permission details, subscription policies, raw quota responses and any benefit information remain in an owner-restricted record outside the repository. No balance/expiry claim is made. No resource was created, provider registered, quota increase requested, role assigned, subscription switched or billing setting changed. The private checks used CLI metadata reads and ARM **GET** requests only. No deployment validation/what-if request was executed.

## Documented deployment path and its limits

| Step | Evidence and remaining contract |
| --- | --- |
| Entra-authenticated ZIP deployment | Microsoft documents `az webapp deploy` with Entra fallback while SCM basic authentication stays disabled. ZIP the contents of the independent `dotnet publish` output, not the repository or a containing directory. This is a documented candidate, not an executed F1 release. [Authentication](https://learn.microsoft.com/en-us/azure/app-service/configure-basic-auth-disable), [ZIP deployment](https://learn.microsoft.com/en-us/azure/app-service/deploy-zip). |
| Operator permissions | Kudu access requires the app-scoped `Microsoft.Web/sites/publish/Action` operation. Listed permission patterns alone do not prove the actual app scope, deny assignments, inherited policy, regional capacity or successful execution. Do not add broad roles as a convenience. [Kudu authorization](https://learn.microsoft.com/en-us/azure/app-service/resources-kudu#rbac-permissions-required-to-access-kudu). |
| Private settings | A future reviewed update can consume a protected local settings file rather than secret command arguments. Use `__` for nested .NET setting names, suppress command output and never export all settings into a public artifact. Platform encryption does not prevent authorized operators/processes from reading values. Actual permissions/delivery remain unverified. [App settings](https://learn.microsoft.com/en-us/azure/app-service/configure-common#configure-app-settings). |
| Explicit key initialization | The current preview `az webapp exec` path targets a running container. Its detached execute result acknowledges submission rather than returning command output/exit status. Kudu and the main app are different environments. An acknowledgement cannot establish successful initialization. [Container command access](https://learn.microsoft.com/en-us/azure/app-service/configure-linux-open-ssh-session), [App Service team explanation](https://azure.github.io/AppService/2026/09/01/azure-cli-2-90-app-service-updates.html#connect-directly-to-linux-web-app-containers). |

### Blocker 1: retained storage and permission semantics

The platform's Linux guidance describes built-in content storage as a network share, documents fixed `777` permissions on `/home`, and says code apps always mount built-in storage regardless of `WEBSITES_ENABLE_APP_SERVICE_STORAGE`. Thus that setting is not a code-host persistence/security control. [App Service Linux guidance](https://github.com/Azure/app-service-linux-docs/blob/master/Things_You_Should_Know/things_you_should_know.md#configuration).

**Project inference:** this does not prove that a candidate child directory satisfies the probe's Unix owner-only check or exclusive lease. The current [key loader](../../tools/EnterpriseCaseIntelligence.ReviewProbe/ProbeKeys.cs) correctly refuses group/other directory permissions. No actual mount was inspected. Repeated `chmod`, a website environment marker or the existence of `/home` is not sufficient evidence to approve this path. The code-store restriction also remains: never place the SQLite case file there.

Before creation review, specify a verifiable storage/isolation contract, including the app/SCM operator boundary, directory metadata and locking behavior. If a managed platform requires a different access-control model, review it explicitly in an ADR with negative tests and actual acceptance criteria; do not silently bypass the current permission check, remove the single-writer lease, use temporary keys or add a paid storage service. Existing local requirements remain enforced.

### Blocker 2: first initialization before normal startup

The current probe's `serve` command requires initialized retained keys and exits when they are absent. A command path requiring a running app therefore does not, by itself, supply a reliable first-initialization procedure. Running a command in Kudu also does not establish the main container's configuration or filesystem view.

Normal startup must explicitly invoke `dotnet EnterpriseCaseIntelligence.ReviewProbe.dll serve`; launching the assembly without a command exits rather than hosting HTTP. The initial resource envelope has no probe startup command. The final release proposal must include that controlled setting as well as a separate first-initialization procedure, not assume the platform's default entry point will select `serve`.

**Project inference:** an explicit operator-only bootstrap sequence must be designed/tested before this envelope is actionable. It must establish the intended runtime/filesystem view, reserve one writer, perform explicit no-overwrite initialization and verify completion independently of an accepted detached command. It must then replace the bootstrap phase with ordinary `serve`, preserving configuration and keys, without overlapping writers. Missing state on later normal startup must still fail. No bootstrap artifact/mode, automatic startup initialization, public key/configuration endpoint or case-authentication bypass is introduced here.

### Blocker 3: quota, policy and approval evidence

Provider registration is a subscription/tenant change, not a catalog read. Azure can register providers during resource deployment, so deployment is not a harmless way to test eligibility. Keep actual provider status and permission/billing details private. Review any needed registration separately after the storage/bootstrap design is concrete; do not register providers or upgrade a plan merely to make the assessment green. [Provider behavior](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-providers-and-types#register-resource-provider).

The final proposal still needs the exact free SKU/OS/region, applicable quota/capacity, inherited policy/add-on effects, credential delivery, startup/replacement settings and ownership of retained evidence/removal. F1 CPU/bandwidth exhaustion can stop requests with platform 403 responses; filesystem exhaustion can fail writes. Neither outcome authorizes a paid upgrade. [Quota enforcement](https://learn.microsoft.com/en-us/azure/app-service/web-sites-monitor#quota-enforcement).

## Next bounded work

1. Resolve the retained-key isolation and explicit bootstrap contracts together, using local disposable fixtures and authoritative platform evidence. If the required guarantees cannot fit the free topology, keep hosting local and continue product work; do not start speculative SQL conversion to work around them.
2. Only after that design is reviewable, resolve private registration/quota/policy evidence and prepare the complete resource/configuration/release proposal. Treat permissions/catalog success as limited evidence, not eligibility or owner approval.
3. Present that concrete proposal for the owner's creation decision. Actual peer/header fencing, cloud key retention and restart/replacement checks remain later acceptance work. Select any server database only after platform/cost fit is established, using EF Core Code First and explicit retained-data lifecycle tests.

The [managed-ingress matrix](managed-ingress-verification.md#evidence-and-decision-gates) remains open for platform acceptance. The [$0 recurring-cost rule](zero-cost-review-hosting.md#cost-boundary), local case workflow and private identity verification continue unchanged.
