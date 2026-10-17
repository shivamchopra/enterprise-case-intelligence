using EnterpriseCaseIntelligence.Api.Cases;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<InMemoryCaseStore>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    Results.Problem(statusCode: 500, title: "Internal Server Error", type: "about:blank",
        extensions: new Dictionary<string, object?> { ["code"] = "internal_error" }).ExecuteAsync(context)));

app.MapGet("/", () => new { service = "Enterprise Case Intelligence", status = "foundation" });
app.MapHealthChecks("/health");
app.MapCaseEndpoints();

app.Run();

public partial class Program { }
