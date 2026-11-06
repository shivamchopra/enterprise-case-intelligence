# Frozen format-5 SQLite catalogs

The four catalogs record `sqlite_schema` from the format-5 executable at `5ade030`. The fresh catalog comes from `init-store`. The adopted catalogs come from `upgrade-store` run against the corresponding frozen format-4 families, which retain the original format-1, format-2 or format-3 table definitions. The test fixture populates these catalogs with synthetic retained values and historical EF metadata. Keep these catalogs independent of the current EF model and migrations.
