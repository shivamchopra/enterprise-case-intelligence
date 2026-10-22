# Contributing

Keep changes focused and consistent with the current phase. Use a branch or fork as appropriate and explain the behavior or design change when submitting it.

## Persistence approach

Use **EF Core Code First** for persistence wherever supported: C# entities, `DbContext`/Fluent API configuration, LINQ queries, tracked writes and EF-generated migrations with a checked-in model snapshot. This is the owner's chosen direction for new work and the [next task to convert the current store](docs/tasks/ef-core-code-first-adoption.md).

Prefer EF APIs and generated migration operations over hand-written SQL/DDL. EF-generated SQL is normal. Keep hand-written SQL only for a demonstrated provider limitation, required integrity/compatibility operation or historical/fault-injection fixture that EF cannot express. Explain each exception and its validation; parameterize runtime values. Preserve explicit offline upgrades and existing data/transaction contracts. See [AGENTS.md](AGENTS.md) for the complete repository instructions.

## Validation

Before submitting, run from the repository root:

```sh
dotnet restore EnterpriseCaseIntelligence.slnx
dotnet format EnterpriseCaseIntelligence.slnx --verify-no-changes --no-restore
dotnet build EnterpriseCaseIntelligence.slnx --configuration Release --no-restore
dotnet test EnterpriseCaseIntelligence.slnx --configuration Release --no-build
```

API tests create disposable SQLite stores and launch API child processes on loopback ports; no manually initialized store or external service is needed.

The UpgradeFixture project under `tests/` is an executable fixture for deterministic upgrade interruption and lost-completion tests. Keep its pipe barriers/failure controls out of production commands. Use frozen version-1 and version-2 fixtures for compatibility tests; never run upgrade validation against user data.

Add tests when behavior changes. Update documentation when behavior or design changes, and add or update ADRs for meaningful architecture decisions. Keep planned functionality clearly distinguished from implemented functionality.

Never contribute credentials, secrets, proprietary code or real employer/customer data. Use synthetic concepts only. Report security concerns through the process in [SECURITY.md](SECURITY.md).
