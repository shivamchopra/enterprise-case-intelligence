using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Api.Security;
using EnterpriseCaseIntelligence.Api.Workspace;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var mode = args.FirstOrDefault() is "init-store" or "upgrade-store" or "inspect-store" ? args[0] : null;
var builder = WebApplication.CreateBuilder(mode is not null ? args[1..] : args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(services => new CaseDatabase(
    services.GetRequiredService<IConfiguration>()["CaseStorage:DatabasePath"], services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<SqliteCaseStore>();
builder.Services.AddCaseSecurity();

await using var app = builder.Build();

SecuritySettings? security = null;
if (mode is null)
{
    try
    {
        security = app.Services.GetRequiredService<SecuritySettings>();
        if (!security.LocalDemo)
        {
            var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("startup-check");
            var probe = Guid.NewGuid().ToString("N");
            if (protector.Unprotect(protector.Protect(probe)) != probe) throw new System.Security.Cryptography.CryptographicException();
        }
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
    { Console.Error.WriteLine("Security configuration unavailable or incompatible. Select explicit loopback LocalDemo or fully configured HTTPS Oidc mode."); return 1; }
}

try
{
    var database = app.Services.GetRequiredService<CaseDatabase>();
    if (mode is not null)
    {
        switch (mode)
        {
            case "init-store": database.Initialize(); Console.WriteLine("Case store initialized."); break;
            case "upgrade-store": database.Upgrade(); Console.WriteLine("Case store upgraded."); break;
            case "inspect-store": Console.WriteLine($"Case store version: {database.Inspect()}."); break;
        }
        return 0;
    }

    database.ValidateStartup();
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or DbUpdateException)
{
    Console.Error.WriteLine("Case store unavailable or incompatible. Check CaseStorage:DatabasePath and permissions. Stop hosting before inspect-store or upgrade-store; use init-store only for a new file.");
    return 1;
}

app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    Results.Problem(statusCode: 500, title: "Internal Server Error", type: "about:blank",
        extensions: new Dictionary<string, object?> { ["code"] = "internal_error" }).ExecuteAsync(context)));
app.UseCaseSecurity(security!);
app.MapSecurity(security!);

app.MapGet("/", () => new { service = "Enterprise Case Intelligence", status = "foundation" });
app.MapHealthChecks("/health");
app.MapCaseEndpoints();
app.MapWorkspace();

await app.RunAsync();
return 0;

public partial class Program { }
