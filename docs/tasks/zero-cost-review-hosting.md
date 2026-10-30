# Assess a review host with no recurring service charges

**Status: cost constraint accepted; free cloud compatibility unverified; no deployment provisioned.** [ADR 0012](../adr/0012-gate-cloud-hosting-on-zero-recurring-cost.md) keeps the current workspace local while a sustainable free route is assessed. This updates the earlier [single-VM recommendation](review-hosting-preparation.md), which remains a technical fit if a future costed deployment is separately authorized.

## Cost boundary

Use **$0 out-of-pocket recurring hosting cost** as the current requirement. Temporary credits are not the operating model. Do not buy a domain, deploy paid compute/network/storage, enable paid overage, change subscription spending protection or upgrade a free plan to keep a demo online. Keep actual account identifiers, balances, eligibility/expiry records and reviewer details private.

Microsoft's introductory Azure account credit is normally **$200 for 30 days**, distinct from renewable free-service allowances. An actual balance and expiry must be checked privately; this assessment makes no claim about a particular account's remaining benefits. [Azure account terms](https://azure.microsoft.com/en-us/pricing/purchase-options/azure-account).

Azure budgets notify on actual/forecast costs but do **not** stop consumption. Deallocating a VM stops its instance charge while disks/networking can still incur charges. Neither a budget alert nor an evening shutdown is proof of a zero-cost complete environment. [Budget behavior](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets), [VM billing states](https://learn.microsoft.com/en-us/azure/virtual-machines/states-billing).

## Options assessed on 2026-10-26

| Route | Cost/duration evidence | Fit and current decision |
| --- | --- | --- |
| Existing local workspace and private Entra registration | No additional cloud compute/storage purchase is required for the implemented workflow. | Continue development and verification here. Keep the machine loopback-only; do not expose LocalDemo through a tunnel or public interface. |
| Linux VM + retained disk + HTTPS proxy | Technically fits current SQLite. Free VM benefits are conditional and time limited; networking, disks and recovery must also be covered. | Earlier technical recommendation is conditional on separately approved full-environment costs. No VM is selected/provisioned under this constraint. |
| App Service F1 + Azure SQL free tier | F1 is free shared compute with resource quotas. SQL supplies renewable monthly free amounts, with a setting to pause when exhausted. | Candidate for a low-traffic demonstration after provider/ingress/recovery work. Not runnable with the current SQLite/proxy assumptions. |
| App Service/Container Apps + unchanged SQLite | A free compute allowance does not establish a supported retained local filesystem. | Do not deploy the current file onto a share, ephemeral container filesystem or assumed persistent app-content path. |

### Free-tier candidate and limits

App Service's Free plan uses shared compute and cannot scale out. CPU/bandwidth quota exhaustion stops the app until reset and can produce platform **403** responses before the application runs; memory/filesystem quotas also apply. It is intended for development/testing. Verify the exact F1 plan, OS/runtime, region and default HTTPS hostname before selection. Do not enable paid add-ons or a higher plan to work around limits. [App Service plans](https://learn.microsoft.com/en-us/azure/app-service/overview-hosting-plans), [quota enforcement](https://learn.microsoft.com/en-us/azure/app-service/web-sites-monitor).

Azure SQL Database's current free tier includes **100,000 vCore-seconds, 32 GB of data and 32 GB of backup storage per database per month**, renewable for the subscription's lifetime. Explicit free-limit configuration is required: a regular serverless database is billable. The intended exhaustion behavior is **AutoPause**, never **BillOverUsage**. An exhausted database may be unavailable until the next month; free compute is not an always-on allowance. [Free-tier configuration](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-tier?view=azuresql).

The SQL free tier is available only for new databases; restoring/copying a database into the free tier is not supported. With free-limit AutoPause, point-in-time retention is limited to seven days with local redundancy. These constraints must shape the recovery design; a managed-backup feature alone does not prove the project's proposed RPO/RTO or a zero-cost replacement-host exercise. [SQL free-tier FAQ](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-tier-faq?view=azuresql).

The product terms above establish candidate features, not subscription eligibility, complete-stack cost or application compatibility. No exact paid-VM quote, remaining credit, regional capacity reservation or promise of uninterrupted service is made here. Recheck product terms and the complete proposed configuration before provisioning.

### Read-only catalog observations

On 2026-10-26, Azure CLI catalog queries advertised Linux `DOTNETCORE|10.0` as active and listed both East US and Canada Central for F1 Linux. These are catalog observations, not an allocation, subscription quota/SQL-benefit check or proof that the combined application works on F1. No plan, application or database was created. The following queries can refresh the observations without provisioning:

```sh
az webapp list-runtimes --os linux --output json
az appservice list-locations --sku F1 --linux-workers-enabled --output json
```

See the [supported-runtime procedure](https://learn.microsoft.com/en-us/azure/app-service/configure-language-dotnetcore#show-net-core-version), [runtime command](https://learn.microsoft.com/en-us/cli/azure/webapp#az-webapp-list-runtimes) and [region command](https://learn.microsoft.com/en-us/cli/azure/appservice#az-appservice-list-locations). Keep any separate account/billing/eligibility checks private. The baseline stays .NET 10; a cheaper plan is not a reason to downgrade it.

## Ordered delivery gates

1. **Preserve the working local baseline.** Keep .NET 10, synthetic data, private Entra configuration, EF Code First and all existing SQLite/recovery/browser guarantees. No hosting deadline justifies resetting data or weakening authentication.
2. **Define the managed-host compatibility contract.** Specify exact App Service HTTPS/host/forwarding trust, fail-closed configuration, default-hostname callback, protected retained session keys and restricted credential delivery. Confirm .NET 10/OS/F1 availability and private subscription eligibility without creating resources. Do not enable unrestricted automatic forwarding as a shortcut.
3. **Design the SQL provider slice before implementing it.** Select it through a provider ADR only when its platform/cost fit is established. Specify EF-generated provider migrations and explicit offline initialization/adoption, ordinal title/key behavior, bounded pagination, transaction/concurrency outcomes, retained timestamps/history and original creation-result replay. Preserve existing SQLite support/fixtures; tests need a real SQL provider and disposable synthetic data.
4. **Prepare a complete resource/cost manifest.** List every resource and exact SKU, free-limit setting, quota/failure behavior, key/credential location, release procedure and removal/retention owner. Avoid optional billed diagnostics, private networking, certificates, domains and backup services unless separately costed and authorized. Budget notifications may supplement service-enforced free limits, never replace them.
5. **Prove recovery and quota behavior.** Address the free-tier restore restrictions, pauses/resume latency and safe request failure without automatic write replay or state reset. Revisit hosted availability/RPO/RTO acceptance explicitly if the free topology cannot meet the proposal. Do not silently mark SH-09–SH-13 complete.
6. **Review a concrete deployment before creation.** Verify eligibility and the complete no-overage configuration privately, then present the exact manifest, limits and unresolved risks for the owner's final provisioning decision. If it cannot meet the cost boundary, continue locally. Do not consume introductory credit as an implicit fallback.

The [managed-host compatibility assessment and read-only catalog tool](app-service-compatibility.md) now make the gaps explicit. [ADR 0013](../adr/0013-load-protection-certificates-from-private-configuration.md) now implements and locally tests the restricted-configuration certificate source with encrypted retained keys. The [independent operator probe](../usage/operator-review-probe.md) is now implemented/tested locally against the [managed-ingress contract](managed-ingress-verification.md). The [2026-10-29 deployment-path assessment](free-tier-deployment-preflight.md) identifies retained-storage and explicit first-initialization blockers; private reads have not established eligible allocation. The [guarded preparation/initialization sequence](../usage/operator-review-probe.md#explicit-preparation-and-initialization) is now locally tested. Next complete exact private eligibility and a concrete preparation-only proposal; actual storage/isolation remain an acceptance gate and initialization must stop if they fail. Still prove managed-ingress trust and actual free-tier credential delivery/retention; a justified SQL provider design follows only when its platform/cost fit is established. Cloud resource creation and a public endpoint remain later gates. The [hosting preparation task](review-hosting-preparation.md) retains the security, five-user workload, diagnostics and recovery checks still required before invited access.
