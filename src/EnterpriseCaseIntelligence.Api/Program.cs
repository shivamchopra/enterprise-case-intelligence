using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.Data.Sqlite;

var initializing = args.FirstOrDefault() == "init-store";
var builder = WebApplication.CreateBuilder(initializing ? args[1..] : args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(services => new CaseDatabase(
    services.GetRequiredService<IConfiguration>()["CaseStorage:DatabasePath"]));
builder.Services.AddSingleton<SqliteCaseStore>();

await using var app = builder.Build();

try
{
    var database = app.Services.GetRequiredService<CaseDatabase>();
    if (initializing)
    {
        database.Initialize();
        Console.WriteLine("Case store initialized.");
        return 0;
    }

    database.ValidateStartup();
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or SqliteException)
{
    Console.Error.WriteLine("Case store unavailable or incompatible. Check CaseStorage:DatabasePath and permissions; use init-store only for a new file.");
    return 1;
}

app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    Results.Problem(statusCode: 500, title: "Internal Server Error", type: "about:blank",
        extensions: new Dictionary<string, object?> { ["code"] = "internal_error" }).ExecuteAsync(context)));

app.MapGet("/", () => new { service = "Enterprise Case Intelligence", status = "foundation" });
app.MapHealthChecks("/health");
app.MapCaseEndpoints();

await app.RunAsync();
return 0;

public partial class Program { }
