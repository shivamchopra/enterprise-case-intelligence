using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests;

internal static class VersionFourCaseStore
{
    // Catalogs captured from main 97c67ec before any model changes, using the released
    // initializer and each supported 1/2/3 adoption path. Never derive these from the new model.
    internal static (CaseSnapshot[] Cases, Dictionary<Guid, Guid> Bindings) Initialize(
        TemporaryCaseDatabase fixture, string family = "fresh", int count = 7, string encoding = "UTF-8", string patch = "10.0.14")
        => InitializeFromCatalog(fixture, family, count, encoding, patch, "FormatFour");

    internal static (CaseSnapshot[] Cases, Dictionary<Guid, Guid> Bindings) InitializeFromCatalog(
        TemporaryCaseDatabase fixture, string family, int count, string encoding, string patch, string catalog)
    {
        using var source = new TemporaryCaseDatabase(false);
        var data = VersionThreeCaseStore.Initialize(source, count, encoding);
        var history = VersionTwoCaseStore.HistoryFields(source);
        var schemas = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", catalog, family + ".json")))!;
        using (new FileStream(fixture.Path, FileMode.CreateNew)) { }
        fixture.Execute($"PRAGMA encoding=\"{encoding}\"");
        foreach (var name in new[] { "cases", "case_status_history", "case_creation_keys", "__EFMigrationsHistory" }) fixture.Execute(schemas[name]);
        foreach (var record in data.Cases)
            fixture.Execute("INSERT INTO cases VALUES ($id,$title,$description,$status)",
                ("$id", record.Id.ToString("D")), ("$title", record.Title), ("$description", record.Description), ("$status", record.Status.ToString()));
        foreach (var row in history)
            fixture.Execute("INSERT INTO case_status_history VALUES ($id,$sequence,$kind,$from,$to,$time)",
                ("$id", row[0]), ("$sequence", long.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture)),
                ("$kind", row[2]), ("$from", row[3] == "<NULL>" ? DBNull.Value : row[3]), ("$to", row[4]), ("$time", row[5]));
        foreach (var (key, id) in data.Bindings)
            fixture.Execute("INSERT INTO case_creation_keys VALUES ($key,$id)", ("$key", key.ToString("D")), ("$id", id.ToString("D")));
        fixture.Execute("INSERT INTO __EFMigrationsHistory VALUES ('20261022194811_InitialCaseStore',$patch); PRAGMA user_version=4", ("$patch", patch));
        return data;
    }
}
