# Contributing

Keep changes focused and consistent with the current phase. Use a branch or fork as appropriate and explain the behavior or design change when submitting it.

## Persistence approach

Use **EF Core Code First** for persistence wherever supported: C# entities, `DbContext`/Fluent API configuration, LINQ queries, tracked writes and EF-generated migrations with a checked-in model snapshot. This is the owner's chosen direction for new work and the [implemented store adoption](docs/tasks/ef-core-code-first-adoption.md).

Prefer EF APIs and generated migration operations over hand-written SQL/DDL. EF-generated SQL is normal. Keep hand-written SQL only for a demonstrated provider limitation, required integrity/compatibility operation or historical/fault-injection fixture that EF cannot express. Explain each exception and its validation; parameterize runtime values. Preserve explicit offline upgrades and existing data/transaction contracts. See [AGENTS.md](AGENTS.md) for the complete repository instructions.

## Validation

Before submitting, run from the repository root:

```sh
dotnet restore EnterpriseCaseIntelligence.slnx
dotnet tool restore
dotnet format EnterpriseCaseIntelligence.slnx --verify-no-changes --no-restore
dotnet build EnterpriseCaseIntelligence.slnx --configuration Release --no-restore
dotnet ef migrations has-pending-model-changes --project src/EnterpriseCaseIntelligence.Api --configuration Release --no-build
# Once per Playwright update; Linux may require install --with-deps chromium.
dotnet exec --runtimeconfig tests/EnterpriseCaseIntelligence.Api.Tests/bin/Release/net10.0/EnterpriseCaseIntelligence.Api.Tests.runtimeconfig.json tests/EnterpriseCaseIntelligence.Api.Tests/bin/Release/net10.0/Microsoft.Playwright.dll install chromium
dotnet test EnterpriseCaseIntelligence.slnx --configuration Release --no-build
```

API tests create disposable SQLite stores and launch API child processes on loopback ports; no manually initialized store or external service is needed.

Workspace tests use the pinned Microsoft.Playwright library and its Chromium download. After installing it, the normal test command includes the real-browser journey. For a browser-free backend check use `--filter 'Category!=Browser'`; browser coverage is still required before submitting UI changes. CI runs backend checks and a separate `Category=Browser` job against the published API. The application itself needs neither Chromium nor Node. See [ADR 0008](docs/adr/0008-serve-a-local-browser-case-workspace.md) for the boundary and test approach.

The UpgradeFixture project under `tests/` is an executable fixture for deterministic upgrade interruption and lost-completion tests. Keep its pipe barriers/failure controls out of production commands. Use frozen version-1, version-2 and version-3 fixtures for compatibility tests; never run upgrade validation against user data.

Add tests when behavior changes. Update documentation when behavior or design changes, and add or update ADRs for meaningful architecture decisions. Keep planned functionality clearly distinguished from implemented functionality.

Never contribute credentials, secrets, proprietary code or real employer/customer data. Use synthetic concepts only. Report security concerns through the process in [SECURITY.md](SECURITY.md).

For a model change, restore the pinned local tool, update the Fluent API model and run:

```sh
dotnet ef migrations add DescriptiveChange --project src/EnterpriseCaseIntelligence.Api --output-dir Cases/Migrations
```

Inspect the generated operations and snapshot, and test the compatibility/version amendment against retained files. The design-time factory uses only an in-memory SQLite configuration; it never selects a user store. Apply changes through the application's explicit offline commands in the walkthrough. See [ADR 0007](docs/adr/0007-adopt-ef-core-code-first.md) for transaction ownership and documented SQL exceptions.
