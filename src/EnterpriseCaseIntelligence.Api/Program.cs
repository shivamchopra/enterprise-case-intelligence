var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/", () => new { service = "Enterprise Case Intelligence", status = "foundation" });
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
