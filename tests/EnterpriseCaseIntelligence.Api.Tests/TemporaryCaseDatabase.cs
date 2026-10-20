using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCaseIntelligence.Api.Tests;

public sealed class TemporaryCaseDatabase : IDisposable
{
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("eci-tests-").FullName;
    public string Path => System.IO.Path.Combine(DirectoryPath, "cases.db");
    public CaseDatabase Database => new(Path);

    public TemporaryCaseDatabase(bool initialize = true)
    {
        if (initialize)
        {
            Database.Initialize();
        }
    }

    public WebApplicationFactory<Program> CreateFactory(TimeProvider? clock = null) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CaseStorage:DatabasePath"] = Path }));
            if (clock is not null) builder.ConfigureServices(services => services.AddSingleton(clock));
        });

    public void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    public long Count()
        => (long)Scalar("SELECT count(*) FROM cases")!;

    public object? Scalar(string sql)
    {
        using var connection = Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
