# Contributing

Keep changes focused and consistent with the current phase. Use a branch or fork as appropriate and explain the behavior or design change when submitting it.

## Persistence approach

Use **EF Core Code First** for persistence wherever supported: C# entities, `DbContext`/Fluent API configuration, LINQ queries, tracked writes and EF-generated migrations with a checked-in model snapshot. This is the owner's chosen direction for new work and the [implemented store adoption](docs/tasks/ef-core-code-first-adoption.md).

Prefer EF APIs and generated migration operations over hand-written SQL/DDL. EF-generated SQL is normal. Keep hand-written SQL only for a demonstrated provider limitation, required integrity/compatibility operation or historical/fault-injection fixture that EF cannot express. Explain each exception and its validation; parameterize runtime values. Preserve explicit offline upgrades and existing data/transaction contracts. See [AGENTS.md](AGENTS.md) for the complete repository instructions.

Keep EF Core runtime/provider and Design package updates together, with the direct Microsoft.Data.Sqlite reference and pinned `dotnet-ef` tool on the matching supported release. Dependabot groups related minor/patch package updates; review the local tool manifest alongside them. Run the full gates and retained-store/replay checks before merging. The compatibility badge summarizes other repositories' CI outcomes; it does not replace this repository's checks. Preserve generated historical migrations/snapshots when no model change is needed. A newer EF patch can stamp its version on newly initialized/adopted stores; the existing metadata validator can refuse those files under an older executable, so do not assume downgrade compatibility.

## Validation

Before submitting, run from the repository root:

```sh
dotnet restore EnterpriseCaseIntelligence.slnx
dotnet tool restore
dotnet format EnterpriseCaseIntelligence.slnx --verify-no-changes --no-restore
dotnet build EnterpriseCaseIntelligence.slnx --configuration Release --no-restore
dotnet build tools/ReviewHostPreflight.cs --configuration Release
dotnet run --file tools/ReviewHostPreflight.cs --configuration Release --no-build -- --proposal infra/review-probe/azuredeploy.json
dotnet ef migrations has-pending-model-changes --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build
# Once per Playwright update; Linux may require install --with-deps chromium.
dotnet exec --runtimeconfig tests/EnterpriseCaseIntelligence.Api.Tests/bin/Release/net10.0/EnterpriseCaseIntelligence.Api.Tests.runtimeconfig.json tests/EnterpriseCaseIntelligence.Api.Tests/bin/Release/net10.0/Microsoft.Playwright.dll install chromium
dotnet test EnterpriseCaseIntelligence.slnx --configuration Release --no-build
```

API tests create disposable SQLite stores, generated test certificates/OIDC protocol fixtures and API child processes on loopback ports; no manually initialized store or external service is needed.

The independent [operator probe](docs/usage/operator-review-probe.md) is built with the solution. Its tests use only disposable certificates/credentials and real child processes on loopback, including preparation without keys, separate initialization, read-only key proof, restart/replacement and competing-writer checks. CI publishes the separate probe artifact and sets `ECI_TEST_PROBE_ASSEMBLY` for those tests. To check a local publish, run `dotnet publish tools/EnterpriseCaseIntelligence.ReviewProbe --configuration Release --no-build --no-restore --output /absolute/temporary/probe-artifact`, then set that environment variable to its `EnterpriseCaseIntelligence.ReviewProbe.dll` for the test command. No cloud account, resource creation, real identity or private local-review state is involved.

Workspace tests use the pinned Microsoft.Playwright library and its Chromium download. After installing it, the normal test command includes the real-browser journey. For a browser-free backend check use `--filter 'Category!=Browser'`; browser coverage is still required before submitting UI changes. CI runs backend checks and a separate `Category=Browser` job: LocalDemo journeys use the published API; authenticated journeys use direct HTTPS and a real HTTPS-to-HTTP proxy fixture with test-only OIDC metadata/backchannel injection. The application itself needs neither Chromium nor Node. See [ADR 0008](docs/adr/0008-serve-a-local-browser-case-workspace.md) for the boundary and test approach.

The UpgradeFixture project under `tests/` is an executable fixture for deterministic upgrade interruption and lost-completion tests. Keep its pipe barriers/failure controls out of production commands. Use frozen version-1, version-2 and version-3 fixtures for compatibility tests; never run upgrade validation against user data.

Add tests when behavior changes. Update documentation when behavior or design changes, and add or update ADRs for meaningful architecture decisions. Keep planned functionality clearly distinguished from implemented functionality.

The optional [hosting catalog checker](docs/tasks/app-service-compatibility.md#run-the-catalog-checker) is a .NET 10 file-based operator tool, not an API runtime dependency. CI builds its standalone entry point and tests its linked implementation with disposable responses; no real Azure CLI login is required. The same tool checks the fixed [undeployed probe envelope](infra/review-probe/README.md) offline; negative tests reject paid/additional resources, weakened boundaries and secret-bearing template inputs/outputs. Neither catalog nor proposal success proves Azure/provider validation, deployment readiness, free-tier eligibility or owner approval.

Never contribute credentials, secrets, proprietary code or real employer/customer data. Use synthetic concepts only. Report security concerns through the process in [SECURITY.md](SECURITY.md).

For a model change, restore the pinned local tool, update the Fluent API model and run:

```sh
dotnet ef migrations add DescriptiveChange --project src/EnterpriseCaseIntelligence.Api --output-dir Cases/Migrations
```

Inspect the generated operations and snapshot, and test the compatibility/version amendment against retained files. The design-time factory uses only an in-memory SQLite configuration; it never selects a user store. Apply changes through the application's explicit offline commands in the walkthrough. See [ADR 0007](docs/adr/0007-adopt-ef-core-code-first.md) for transaction ownership and documented SQL exceptions.
