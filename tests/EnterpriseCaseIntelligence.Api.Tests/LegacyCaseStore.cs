using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests;

internal static class LegacyCaseStore
{
    // Frozen schema emitted by the Phase 3 executable, without using current initialization.
    private const string Schema = """
        CREATE TABLE cases (
            id TEXT NOT NULL PRIMARY KEY,
            title TEXT NOT NULL,
            description TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('New', 'InReview', 'Closed'))
        ) STRICT
        """;

    internal static CaseSnapshot[] Initialize(TemporaryCaseDatabase fixture, int count = 3, string encoding = "UTF-8")
    {
        using (new FileStream(fixture.Path, FileMode.CreateNew)) { }
        fixture.Execute($"PRAGMA encoding=\"{encoding}\"; " + Schema + "; PRAGMA user_version=1");
        var records = new List<CaseSnapshot>();
        for (var index = 0; index < count; index++)
        {
            var record = new CaseSnapshot(Guid.Parse($"{index + 1:x8}-aaaa-4bbb-8ccc-dddddddddddd"),
                $"Synthetic legacy {index}", "Synthetic 'quoted' 😀\0description", (CaseStatus)(index % 3));
            fixture.Execute("INSERT INTO cases VALUES ($id,$title,$description,$status)",
                ("$id", record.Id.ToString("D")), ("$title", record.Title),
                ("$description", record.Description), ("$status", record.Status.ToString()));
            records.Add(record);
        }

        return records.ToArray();
    }
    internal static void AssertRecords(TemporaryCaseDatabase fixture, CaseSnapshot[] expected)
    {
        using var connection = fixture.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,description,status FROM cases ORDER BY id";
        using var rows = command.ExecuteReader();
        foreach (var record in expected)
        {
            Assert.True(rows.Read());
            Assert.Equal(record.Id.ToString("D"), rows.GetString(0));
            Assert.Equal(record.Title, rows.GetString(1));
            Assert.Equal(record.Description, rows.GetString(2));
            Assert.Equal(record.Status.ToString(), rows.GetString(3));
        }
        Assert.False(rows.Read());
    }
}
