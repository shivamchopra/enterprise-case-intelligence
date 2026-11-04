# Frozen released format-4 catalogs

These exact SQLite catalog strings were captured before the priority model change from main `97c67eca2c533a40c27522e6931f09a4f193bc36` (.NET 10, EF 10.0.14), using disposable synthetic files:

- `fresh.json`: released `CaseDatabase.Initialize` plus keyed synthetic creation.
- `adopted-1/2/3.json`: frozen supported version-1/2/3 fixtures followed by the released `CaseDatabase.Upgrade`.

Capture queried `name,sql` from `sqlite_schema` excluding `sqlite_*`; only table definitions are retained. No user store or private identity was read. The test fixture supplies deterministic values/history/bindings independently. Do not regenerate these from the latest model or new initializer: they are old-source compatibility evidence, including mixed legacy/EF table layouts.
