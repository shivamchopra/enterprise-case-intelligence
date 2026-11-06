using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests;

internal static class VersionFiveCaseStore
{
    internal static (CaseSnapshot[] Cases, Dictionary<Guid, Guid> Bindings) Initialize(
        TemporaryCaseDatabase fixture, string family = "fresh", int count = 7, string encoding = "UTF-8")
    {
        var data = VersionFourCaseStore.InitializeFromCatalog(fixture, family, count, encoding, "10.0.13", "FormatFive");
        var schemas = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "FormatFive", family + ".json")))!;
        fixture.Execute(schemas["case_priorities"]);
        for (var index = 0; index < data.Cases.Length; index++)
            fixture.Execute("INSERT INTO case_priorities VALUES ($id,$priority)",
                ("$id", data.Cases[index].Id.ToString("D")), ("$priority", PriorityAt(index).ToString()));
        fixture.Execute("INSERT INTO __EFMigrationsHistory VALUES ($migration,'10.0.14'); PRAGMA user_version=5",
            ("$migration", "20261104043724_AddCasePriority"));
        return data;
    }

    internal static CasePriority PriorityAt(int index) => (index % 3) switch
    {
        0 => CasePriority.High,
        1 => CasePriority.Low,
        _ => CasePriority.Normal
    };
}
