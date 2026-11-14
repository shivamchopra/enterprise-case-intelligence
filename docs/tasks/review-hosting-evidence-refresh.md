# Review hosting evidence refresh

**November 9, 2026. Provisioning remains on hold.** The read-only checks returned more regional quota evidence than the earlier assessment. Exact Linux F1 allocation, verified spending protection and effective policy at the proposed scope remain open. The preparation-only probe ZIP passed a local rehearsal. [Zero-cost hosting decision](zero-cost-review-hosting.md) · [Preparation-only proposal](preparation-only-deployment-proposal.md)

## Azure evidence

| Check | What the evidence supports |
| --- | --- |
| Public pricing and catalog | Microsoft still lists Linux F1 as a free shared trial plan. The project catalog check advertises Linux .NET 10 and F1 in both candidate regions. [Linux pricing](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/) · [Catalog checker](app-service-compatibility.md#run-the-catalog-checker) |
| Regional and subscription reads | The regional usage calls now return free-tier quota signals. The usage API describes subscription core usage, and the subscription SKU response did not supply affirmative Linux F1 allocation evidence. The next eligibility decision needs an explicit result for the exact Linux SKU, region and scope. [Regional usage API](https://learn.microsoft.com/en-us/rest/api/appservice/get-usages-in-location/list?view=rest-appservice-2025-05-01) · [Subscription SKU API](https://learn.microsoft.com/en-us/rest/api/appservice/list-skus/list-skus?view=rest-appservice-2025-05-01) |
| Policy and permissions | Subscription-scoped listings were readable. The proposed scope still needs an effective authorization and inherited policy review, including deny and automatic add-on effects. [Policy assignment API](https://learn.microsoft.com/en-us/rest/api/policy-authorization/policy-assignments/list?view=rest-policy-authorization-2025-11-01) |
| Spending protection | The read-only evidence did not establish active spending protection. Confirm the offer and protection privately in the billing view, and verify the complete resource envelope against the $0 out-of-pocket boundary. [Azure spending limit](https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/spending-limit) |

Keep actual account identifiers, quota values, offer details, role or policy assignments, and raw provider responses in restricted owner evidence. Before proposing creation, record exact Linux F1 eligibility and quota interpretation, the offer and spending protection, effective permissions and policy effects, and every resource or attachment that the reviewed scope could create. The separate platform gates for storage isolation, ingress and key retention still apply. [Private evidence checklist](preparation-only-deployment-proposal.md#private-record-required-before-creation-review)

A November 12 read-only recheck still did not establish exact Linux F1 allocation or active spending protection. The next private checks are the exact SKU and region in **Quotas → App Service (Public Preview)** and the subscription protection status in **Cost Management + Billing**. The project continued locally with an isolated recovery rehearsal. [App Service quota guidance](https://learn.microsoft.com/en-us/troubleshoot/azure/app-service/troubleshoot-non-zone-redundant-quota-requests) · [Local restore rehearsal](../usage/local-restore-rehearsal.md)

## Rehearse the probe ZIP locally

Run from the repository root with the pinned .NET SDK, Bash, `zip` and `unzip`. Choose a new path under a temporary directory.

```sh
dotnet build EnterpriseCaseIntelligence.slnx --configuration Release
REHEARSAL_ROOT="$(mktemp -d)"
bash tools/package-review-probe.sh "$REHEARSAL_ROOT/probe-release"
ECI_TEST_PROBE_ASSEMBLY="$REHEARSAL_ROOT/probe-release/extracted/EnterpriseCaseIntelligence.ReviewProbe.dll" \
  dotnet test tests/EnterpriseCaseIntelligence.Api.Tests --configuration Release --no-build \
  --filter 'FullyQualifiedName~ReviewProbe'
```

The script packages the standalone, framework-dependent publish. It checks the required entry files, rejects unexpected files and symlinks, and compares the extracted contents to the publish output. The archive starts at the publish contents, the layout expected by ZIP deployment. CI runs its probe process tests from the extracted copy on Ubuntu. For a later release review, record the reviewed commit, the archive inventory and its SHA-256 privately. [Microsoft ZIP layout](https://learn.microsoft.com/en-us/azure/app-service/deploy-zip#create-a-project-zip-package)

The rehearsal exercises the extracted executable, local preparation, explicit initialization and retained-key checks. Azure command delivery, `/home` isolation, effective ingress and actual key retention require a separately approved platform test.
