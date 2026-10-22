using EnterpriseCaseIntelligence.Api.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests;

internal static class VersionThreeCaseStore
{
    // Frozen Phase 5 DDL, independent of the EF initializer and adoption path.
    private const string KeySchema = """
        CREATE TABLE case_creation_keys (
            request_key TEXT NOT NULL PRIMARY KEY,
            case_id TEXT NOT NULL UNIQUE,
            FOREIGN KEY (case_id) REFERENCES cases(id),
            CHECK (
                length(request_key) = 36
                AND instr(request_key, char(0)) = 0
                AND substr(request_key, 9, 1) = '-'
                AND substr(request_key, 14, 1) = '-'
                AND substr(request_key, 19, 1) = '-'
                AND substr(request_key, 24, 1) = '-'
                AND length(replace(request_key, '-', '')) = 32
                AND replace(request_key, '-', '') NOT GLOB '*[^0-9a-f]*'
                AND request_key <> '00000000-0000-0000-0000-000000000000'
            )
        ) STRICT
        """;

    internal static (CaseSnapshot[] Cases, Dictionary<Guid, Guid> Bindings) Initialize(
        TemporaryCaseDatabase fixture, int count = 7, string encoding = "UTF-8")
    {
        var records = VersionTwoCaseStore.Initialize(fixture, count, encoding);
        fixture.Execute(KeySchema + "; PRAGMA user_version=3");
        var bindings = new Dictionary<Guid, Guid>();
        for (var index = 1; index < records.Length; index += 2)
        {
            var key = Guid.Parse($"fe{index:x6}-aaaa-4bbb-8ccc-dddddddddddd");
            fixture.Execute("INSERT INTO case_creation_keys VALUES ($key,$id)",
                ("$key", key.ToString("D")), ("$id", records[index].Id.ToString("D")));
            bindings.Add(key, records[index].Id);
        }
        return (records, bindings);
    }
}
