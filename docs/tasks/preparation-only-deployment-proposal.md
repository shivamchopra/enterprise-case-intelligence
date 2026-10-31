# Preparation-only F1 probe proposal

**Prepared on 2026-10-31. Status: reviewable proposal; creation remains on hold.** The [resource envelope](../../infra/review-probe/azuredeploy.json) now fixes initial startup to the independent probe's `prepare` command. Its offline checker rejects other startup commands. This does not establish Azure eligibility, provider acceptance or permission to deploy. No Azure resources or settings were changed for this proposal.

## Purpose and decision

The first cloud experiment would inspect the candidate runtime and retained-directory metadata without delivering protection material or initializing keys. It would answer whether a later retention experiment can satisfy the existing storage/isolation contract. It would not host cases, sign in reviewers or select a server database.

Fresh private reads on 2026-10-31 again advertised Linux .NET 10 and F1 Linux in both East US and Canada Central. Subscription metadata, provider metadata and listed permissions were readable; both regional usage requests remained unavailable. Actual allocation, effective authorization, inherited policy/add-on effects and complete eligibility remain unproven. Raw account/provider/permission responses remain outside Git in restricted owner evidence. No registration, validation/what-if, deployment, quota, role or billing mutation was performed. Unavailable usage is not a zero limit or evidence of eligibility.

**Decision: hold creation.** Complete the private record below before presenting a creation decision. If a provider registration is needed to obtain further evidence, review that exact subscription change first; it is separate from resource creation and cannot be treated as a read-only eligibility query. Registration can add an application to the tenant, and deployment can register providers automatically. [Provider registration](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-providers-and-types#register-resource-provider).

## Resource and cost manifest

| Item | Proposed fixed scope | Cost boundary and failure action |
| --- | --- | --- |
| Resource group | One new, otherwise empty group dedicated to this experiment; owner records its exact name privately | Inventory policy-created or associated resources too. Stop if the complete group/subscription effects cannot meet the cost boundary. |
| App Service plan | One Linux `F1` / `Free`, capacity 1, no per-site scaling | Published free shared compute; never select a paid SKU on allocation failure. |
| Code site | One new .NET 10 Linux site on that plan, default platform HTTPS hostname, HTTPS-only, site/SCM TLS at least 1.2 | No custom domain/certificate, Always On, slots, scale-out, paid diagnostics, network/storage add-on or monitoring service. |
| Publishing children | Exactly SCM and FTP basic-publishing policies, both disabled | Use app-scoped Entra/RBAC tooling; never enable publishing passwords to make a command work. |
| Published artifact | Only the independent probe's Release publish output; contents at `/home/site/wwwroot` | No case API, SQLite file, local-review configuration, source checkout or protection material. |
| Retained state | Initial inspection of a candidate path outside content/artifact roots | No storage-account purchase, key initialization or permission-check exception. Unusable metadata/lease/isolation means stop. |

Microsoft lists F1 as free shared compute with 60 CPU minutes/day, 1 GB RAM and 1 GB storage, without an SLA. East US is the proposed first region, conditional on actual free allocation; the current catalog supplies no price reason to prefer Canada Central. Region substitution requires a refreshed private record, not a paid fallback. These product facts do not establish account eligibility or a subscription-wide spending cap. [Linux pricing](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/).

CPU/bandwidth exhaustion can stop the app with platform 403 until reset; memory exhaustion can stop it temporarily, and filesystem exhaustion fails writes. Treat these as experiment outcomes, never instructions to upgrade. No introductory-credit consumption is an operating assumption. [Quota enforcement](https://learn.microsoft.com/en-us/azure/app-service/web-sites-monitor#quota-enforcement).

## Private record required before creation review

The repository owner is the proposed release, evidence and retirement operator. Keep a restricted, dated record outside Git; do not paste account values, full CLI responses, settings or receipts into the PR. Before creation review, fill every field with evidence or an explicit unresolved result:

| Field | Required evidence |
| --- | --- |
| Scope | Intended subscription/tenant, exact fresh group/plan/site names, region, operator identity and retirement deadline. Confirm names do not refer to existing resources. |
| Free allocation | Exact Linux F1 eligibility and applicable capacity/quota evidence; a successful generic regional core-usage response alone is insufficient. Account offer/expiry and spending protection remain private; temporary credit cannot compensate for an unbounded configuration. |
| Policy and authorization | Effective operations at the actual scope, deny assignments, inherited policy/deploy-if-not-exists effects, paid security/diagnostic attachments and the full ancillary-resource inventory. Listed allow patterns alone are insufficient. |
| Configuration | Actual default HTTPS origin from the control plane; independent environment/revision, random bearer, one explicit backend binding and candidate retained path. No existing local-review values may be reused. |
| Release | Reviewed commit, fresh standalone publish, private artifact manifest/hashes and offline proposal-check result. Inspect the ZIP contents before upload; record the deployment principal and supported Entra command version. |
| Acceptance and removal | Named owner, bounded experiment window, exact stop conditions, private evidence destination/retention and group-only removal procedure. Proposed window: retire within 24 hours of creation unless separately reviewed. |

Do not infer that failed evidence permits deployment as an eligibility test. The [dated platform assessment](free-tier-deployment-preflight.md) remains relevant, including the fixed `/home` permissions and unverified child-directory semantics. No managed-filesystem exception is proposed here.

## Initial configuration and release sequence

This sequence is for a future approved experiment. It is not executed by CI or by this proposal. ARM/provider validation and the creation decision remain separate from the local structural check.

1. Publish only `tools/EnterpriseCaseIntelligence.ReviewProbe` to a fresh local directory and run the required local gates, including process tests against that publish. ZIP the **contents** of that directory. Keep the manifest/hashes and approved template/parameter record privately; no secret-bearing generated files belong in the artifact. [ZIP release guidance](https://learn.microsoft.com/en-us/azure/app-service/deploy-zip).
2. After the private evidence and owner decision, use only the reviewed resource envelope and fresh scope. Check the resulting resource inventory, F1 plan and disabled publishing/logging settings before delivering a bearer. Unexpected policy resources, paid attachments or weaker settings mean stop and reconcile privately. The template's initial app-settings collection must never be reapplied over later private settings.
3. Deliver only the preparation settings below through a restricted local app-settings file, using approved Entra/RBAC tooling with output/debug/request tracing suppressed. Check the platform logging/access configuration before delivery; disabling application logs does not prove that platform/operators cannot see requests or settings. The control plane encrypts app settings at rest, but authorized operators/processes can access them. [App-setting behavior](https://learn.microsoft.com/en-us/azure/app-service/configure-common#configure-app-settings), [deployment authentication](https://learn.microsoft.com/en-us/azure/app-service/configure-basic-auth-disable).
4. Deploy the standalone ZIP with basic publishing still disabled. The fixed initial `siteConfig.appCommandLine` is `dotnet /home/site/wwwroot/EnterpriseCaseIntelligence.ReviewProbe.dll prepare`. The [ARM property](https://learn.microsoft.com/en-us/azure/templates/microsoft.web/2024-11-01/sites) supplies the startup command; actual platform execution and backend port must be verified. A failed start never authorizes initialization or a different startup command.
5. Use the local HTTPS-only `setup-status` client with only the new origin/bearer. Record its bounded response privately and test anonymous denial. A client exit `0` is only a successful fetch. Inspect `state`; both `deploymentReady` and `platformIsolationVerified` remain false. Missing, unsafe or unavailable state supplies no permission to write/create a directory. Do not send the bearer directly to an HTTP listener or bypass TLS.
6. Record actual mount/owner/ancestor/access metadata and app/SCM/operator boundaries through the authorized runtime channel without dumping environment values or credentials. The initial phase does not create a directory or lease. A later deliberate directory preparation needs an accepted storage/access contract; fixed permissive mode bits or unsupported locking mean stop, not repeated `chmod` or a bypass.

| Initial private setting | Value/source and boundary |
| --- | --- |
| `Probe__Enabled` | Literal `true`; explicitly opt in only this disposable probe. |
| `Probe__PublicOrigin` | Actual HTTPS origin, exact host, no path/query/fragment. |
| `Probe__OperatorCredential` | New cryptographic 32-byte random lowercase-hex credential, private file only. Never reuse the local-review identity or a public example. |
| `Probe__EnvironmentId`, `Probe__Revision` | New independent identifiers; retain for a later approved receipt experiment. |
| `Probe__KeyDirectory` | Proposed candidate `/home/eci-probe-keys`, outside `/home/site/wwwroot`; may be missing. Its location is not a claim of persistence, safe permissions or a healthy mount. |
| `ASPNETCORE_URLS` | Proposed single `http://0.0.0.0:8080` backend binding, contingent on the selected built-in worker's port contract. Confirm it before release; a mismatch requires a reviewed configuration correction. It establishes no ingress trust. |
| Certificate/configuration exclusions | Omit all `Security__Certificate*`, `ECI_PROBE_CONFIGURATION`, local-review paths, hosting-startup injection and configured Kestrel endpoints. Initial metadata preparation needs no PFX/password. |

Preserve the envelope's two non-secret settings: automatic forwarding disabled and the storage-intent flag. The latter is not a code-host storage/isolation control. Verify no stale environment override enables forwarding or supplies unintended certificate settings. Keep the private client configuration separate from server settings. The [probe runbook](../usage/operator-review-probe.md) supplies bounds and credential handling rules.

## Stop, retirement and later work

Any uncertain cost/eligibility, unsupported command channel, unsafe storage, unexpected logging, authentication/TLS failure or unaccounted resource is a stop condition. Stop the experiment; preserve bounded private evidence without repeatedly deploying or retrying writes. There is no approved paid fallback, billing change or key reset.

The owner should stop the site and retire the dedicated experiment group within the recorded window. Confirm the group contains only this experiment before deleting it; never delete a reused/shared group. Stopping the site alone is not removal. Reconcile the final inventory and any policy-created resources outside the group, retain a redacted outcome privately for seven days (or explicitly record a different requirement), then remove local credentials/evidence under the owner's policy. Do not change subscription-wide provider registration or billing settings as incidental cleanup. Destruction is a separate explicit operator action, not an automated side effect of a failed check.

Protection-certificate delivery, deliberate empty-directory preparation, separate `init-keys`, independent `keys_verified` proof and controlled stop/restart to `serve` belong to a **later accepted storage/retention experiment**. It must verify the preparation receipt after promotion and replacement with one writer and unchanged retained state, as defined in [ADR 0015](../adr/0015-prepare-probe-storage-before-explicit-initialization.md). This initial envelope deliberately rejects `serve` and `init-keys` as startup commands; promotion needs a separately reviewed configuration update, not template reapplication.

**Next decision:** resolve the private registration/eligibility/policy gaps against this manifest, then present the exact eligible scope for creation approval. If this free topology cannot meet the contract, keep the application local and resume product work. Cloud ingress, protected key retention, a selected EF Code First server provider, recovery and invited-user acceptance remain separate later gates.
