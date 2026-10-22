using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests;

internal static class VersionTwoCaseStore
{
    // Frozen Phase 4 DDL: compatibility fixtures never use the current initializer/upgrade.
    private const string HistorySchema = """
        CREATE TABLE case_status_history (
            case_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence BETWEEN 1 AND 3),
            kind TEXT NOT NULL CHECK (kind IN ('Created', 'Baseline', 'StatusChanged')),
            from_status TEXT CHECK (from_status IS NULL OR from_status IN ('New', 'InReview', 'Closed')),
            to_status TEXT NOT NULL CHECK (to_status IN ('New', 'InReview', 'Closed')),
            recorded_at_utc TEXT NOT NULL,
            PRIMARY KEY (case_id, sequence),
            FOREIGN KEY (case_id) REFERENCES cases(id),
            CHECK (
                (sequence = 1 AND kind = 'Created' AND from_status IS NULL AND to_status = 'New')
                OR (sequence = 1 AND kind = 'Baseline' AND from_status IS NULL)
                OR (sequence BETWEEN 2 AND 3 AND kind = 'StatusChanged' AND from_status IS NOT NULL
                    AND ((from_status = 'New' AND to_status = 'InReview')
                        OR (from_status = 'InReview' AND to_status = 'Closed')))
            )
        ) STRICT
        """;

    internal static CaseSnapshot[] Initialize(TemporaryCaseDatabase fixture, int count = 3, string encoding = "UTF-8")
    {
        var records = LegacyCaseStore.Initialize(fixture, count, encoding);
        fixture.Execute(HistorySchema + "; PRAGMA user_version=2");
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (index % 2 == 0)
            {
                Insert(fixture, record.Id, 1, "Baseline", null, record.Status);
            }
            else
            {
                Insert(fixture, record.Id, 1, "Created", null, CaseStatus.New);
                if (record.Status is CaseStatus.InReview or CaseStatus.Closed)
                    Insert(fixture, record.Id, 2, "StatusChanged", "New", CaseStatus.InReview);
                if (record.Status == CaseStatus.Closed)
                    Insert(fixture, record.Id, 3, "StatusChanged", "InReview", CaseStatus.Closed);
            }
        }
        return records;
    }

    private static void Insert(TemporaryCaseDatabase fixture, Guid id, int sequence, string kind, string? from, CaseStatus to)
        => fixture.Execute("INSERT INTO case_status_history VALUES ($id,$seq,$kind,$from,$to,$time)",
            ("$id", id.ToString("D")), ("$seq", sequence), ("$kind", kind), ("$from", (object?)from ?? DBNull.Value),
            ("$to", to.ToString()), ("$time", $"2026-10-{22 - sequence:D2}T12:00:00.1234567Z"));

    internal static string[][] HistoryFields(TemporaryCaseDatabase fixture)
    {
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT case_id,sequence,kind,from_status,to_status,recorded_at_utc FROM case_status_history ORDER BY case_id,sequence";
        using var reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, 6).Select(i => reader.IsDBNull(i) ? "<NULL>" : reader.GetString(i)).ToArray());
        return rows.ToArray();
    }
}
