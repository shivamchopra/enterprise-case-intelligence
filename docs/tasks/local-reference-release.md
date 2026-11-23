# Prepare the first versioned local release

The first release is a local reference application for synthetic case work. A reviewer should be able to start from one tagged commit, run the browser walkthrough, verify a closure receipt and reproduce the paired-file recovery rehearsal. The candidate tag is `v0.1.0`. No tag or GitHub release is created by this task.

## Release boundary

The release includes the .NET 10 ASP.NET Core host, the browser workspace, the LocalDemo closure feed, the records intake command and the EF Core Code First SQLite stores. It documents authenticated Reader and Caseworker access as a privately verified local capability. It uses synthetic data and one local API process with one case file and one receipt file.

Hosted deployment, Azure resources, billing changes, a public identity configuration, server-managed persistence, broker integration and model-backed assistance remain outside this release. The $0 recurring-cost boundary remains in force.

## Candidate commit and evidence

The current candidate baseline is the merged main commit [`e74c6e8`](https://github.com/shivamchopra/enterprise-case-intelligence/commit/e74c6e8), which includes the roadmap and reviewer walkthrough from PR [#70](https://github.com/shivamchopra/enterprise-case-intelligence/pull/70). The merged change is documentation-only relative to the previously verified application commit. The final tag must identify the reviewed commit after this checklist merges.

The following evidence is available before the final tag decision.

| Check | Evidence | State |
| --- | --- | --- |
| Local browser and intake walkthrough | The browser workflow passed all 81 Chromium tests on the candidate baseline. A clean-checkout walkthrough followed one synthetic case from creation through High priority, In review and Closed. The brief citations opened the case and closing history entry. Two intake pulls produced one receipt followed by zero new receipts at checkpoint `"1"`. | Browser evidence is complete on the candidate baseline. Repeat the exact walkthrough and record its CLI output before tagging. |
| Recovery behavior | The targeted receipt-store and protected-session recovery tests ran against the merged checkout. | 38 passed on the candidate baseline. Repeat the full recovery rehearsal from [the local restore procedure](../usage/local-restore-rehearsal.md). |
| Full repository gates | PR #70 ran 1,037 tests, including 81 Chromium tests, with formatting, EF model checks, synthetic evaluations, packaging and the dependency audit. | Complete on the reviewed head. Repeat the release commands below before tagging. |
| Public documentation | The flagship README, architecture overview, roadmap and profile Featured Project describe the same local workflow and pending hosted review. | Complete after PR #70 and profile PR #4 merged. |

## Release commands

Run from a fresh checkout of the candidate commit with the pinned SDK and a private temporary directory.

```sh
dotnet --version
dotnet restore EnterpriseCaseIntelligence.slnx
dotnet tool restore
dotnet format EnterpriseCaseIntelligence.slnx --verify-no-changes --no-restore
dotnet build EnterpriseCaseIntelligence.slnx --configuration Release --no-restore
dotnet build tools/ReviewHostPreflight.cs --configuration Release
dotnet run --file tools/ReviewHostPreflight.cs --configuration Release --no-build -- --proposal infra/review-probe/azuredeploy.json
dotnet run --file tools/ClosureBriefEvaluation.cs --configuration Release -- --baseline tests/fixtures/closure-brief/cases.json
dotnet ef migrations has-pending-model-changes --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project tools/EnterpriseCaseIntelligence.RecordsIntake --configuration Release --no-build
dotnet test EnterpriseCaseIntelligence.slnx --configuration Release --no-build
```

Install Chromium for the browser job when it is absent, then run the browser filter from [the contributor checks](../../CONTRIBUTING.md#validation). Keep the API and receipt files in disposable locations. The application needs no cloud account for this release.

## Reviewer and recovery checks

1. Follow [the one-case reviewer walkthrough](../usage/reviewer-walkthrough.md) from the candidate checkout. Record the commit, SDK version and the first and second intake results.
2. Follow [the local restore rehearsal](../usage/local-restore-rehearsal.md) with a stopped case file and receipt file copied from the same recovery point. Verify retained history, creation-key replay, the paired checkpoint and the expected `source_conflict` result when a receipt from a later point is presented to an older source.
3. Run the protected session-key restore section with private test configuration. Keep certificates, cookies, identities and raw command output outside the repository.
4. Confirm the working tree has no generated stores, keys, browser artifacts or build files before creating the tag.

## Tag and release record

After the checks above are recorded, the repository owner can create the annotated tag and GitHub release.

```sh
git tag -a v0.1.0 -m "Enterprise Case Intelligence v0.1.0"
git push origin v0.1.0
```

The release notes should link to the [reviewer walkthrough](../usage/reviewer-walkthrough.md), [restore rehearsal](../usage/local-restore-rehearsal.md), [architecture overview](../architecture/overview.md) and [known operating limits](../../README.md#security-and-privacy). State that the release is local, synthetic and single-process. Keep hosted eligibility, cloud storage, recovery targets and real tenant configuration pending.

The tag identifies the exact commit that passed the release checks. A later hosted experiment needs its own approval, provider evidence and release record.
