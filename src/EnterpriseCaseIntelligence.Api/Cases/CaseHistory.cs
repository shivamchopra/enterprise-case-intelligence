using System.Globalization;
using EnterpriseCaseIntelligence.Domain.Cases;
using Microsoft.Data.Sqlite;

namespace EnterpriseCaseIntelligence.Api.Cases;

public sealed record CaseHistoryEntry(int Sequence, string Kind, CaseStatus? FromStatus, CaseStatus ToStatus, string RecordedAtUtc);

internal static class CaseHistory
{
    internal const string Schema = """
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

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    internal static CaseHistoryEntry Record(int sequence, string kind, CaseStatus? from, CaseStatus to, TimeProvider clock)
        => new(sequence, kind, from, to, clock.GetUtcNow().ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, Guid id, CaseHistoryEntry entry)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO case_status_history (case_id, sequence, kind, from_status, to_status, recorded_at_utc)
            VALUES ($id, $sequence, $kind, $from, $to, $time)
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$sequence", entry.Sequence);
        command.Parameters.AddWithValue("$kind", entry.Kind);
        command.Parameters.AddWithValue("$from", (object?)entry.FromStatus?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", entry.ToStatus.ToString());
        command.Parameters.AddWithValue("$time", entry.RecordedAtUtc);
        if (command.ExecuteNonQuery() != 1) throw InvalidHistory();
    }

    internal static IReadOnlyList<CaseHistoryEntry> Read(SqliteConnection connection, SqliteTransaction transaction, Case record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, kind, from_status, to_status, recorded_at_utc FROM case_status_history
            WHERE case_id=$id ORDER BY sequence LIMIT 4
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        var entries = new List<CaseHistoryEntry>(4);
        while (reader.Read())
        {
            var sequence = reader.GetInt64(0);
            if (sequence is < 1 or > 3) throw InvalidHistory();
            entries.Add(new((int)sequence, reader.GetString(1), reader.IsDBNull(2) ? null : Status(reader.GetString(2)),
                Status(reader.GetString(3)), reader.GetString(4)));
        }

        Validate(record.Status, entries);
        return entries.AsReadOnly();
    }

    private static void Validate(CaseStatus current, List<CaseHistoryEntry> entries)
    {
        if (entries.Count is < 1 or > 3) throw InvalidHistory();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Sequence != index + 1 ||
                !DateTimeOffset.TryParseExact(entry.RecordedAtUtc, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _)) throw InvalidHistory();

            if (index == 0)
            {
                if (entry.FromStatus is not null || entry.Kind is not ("Created" or "Baseline") ||
                    (entry.Kind == "Created" && entry.ToStatus != CaseStatus.New)) throw InvalidHistory();
            }
            else
            {
                var previous = entries[index - 1].ToStatus;
                if (entry.Kind != "StatusChanged" || entry.FromStatus != previous ||
                    (previous, entry.ToStatus) is not ((CaseStatus.New, CaseStatus.InReview) or (CaseStatus.InReview, CaseStatus.Closed)))
                    throw InvalidHistory();
            }
        }

        if (entries[^1].ToStatus != current) throw InvalidHistory();
    }

    private static CaseStatus Status(string value) => value switch
    {
        "New" => CaseStatus.New,
        "InReview" => CaseStatus.InReview,
        "Closed" => CaseStatus.Closed,
        _ => throw InvalidHistory()
    };

    private static InvalidDataException InvalidHistory() => new("Invalid stored case history.");
}
