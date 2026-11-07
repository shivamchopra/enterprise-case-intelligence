using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EnterpriseCaseIntelligence.RecordsIntake;
using EnterpriseCaseIntelligence.UpgradeFixture;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EnterpriseCaseIntelligence.Api.Tests.Intake;

internal sealed class IntakeFiles : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("eci-intake-tests-");
    internal string Path => System.IO.Path.Combine(_directory.FullName, "synthetic-receipts.db");

    internal string[] Arguments(string mode, params string[] options) => [mode, "--receipt-file", Path, .. options];

    internal static async Task<IntakeOutcome> InvokeAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await IntakeCommand.RunAsync(args, output, error);
        return new IntakeOutcome(code, output.ToString(), error.ToString());
    }

    internal static async Task<IntakeOutcome> RunProcessAsync(string[] args, IReadOnlyDictionary<string, string>? environment = null)
    {
        var published = Environment.GetEnvironmentVariable("ECI_TEST_INTAKE_ASSEMBLY");
        using var process = Start(published ?? typeof(IntakeCommand).Assembly.Location, environment, args,
            testOutput: published is null);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await ExitAsync(process);
        return new IntakeOutcome(process.ExitCode, await output, await error);
    }

    internal static Process StartFixture(params string[] args)
        => Start(typeof(UpgradeFixtureProgram).Assembly.Location, null, args);

    private static Process Start(string assembly, IReadOnlyDictionary<string, string>? environment, string[] args, bool testOutput = false)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        // The shared test output contains the API's resolved SQLite/EF dependencies.
        // The standalone published artifact uses its own manifest.
        if (testOutput)
        {
            var api = typeof(Program).Assembly.Location;
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(System.IO.Path.ChangeExtension(api, ".runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(System.IO.Path.ChangeExtension(api, ".deps.json"));
        }

        start.ArgumentList.Add(assembly);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
        return Process.Start(start)!;
    }

    private static async Task ExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    internal ReceiptDbContext OpenContext()
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        return new ReceiptDbContext(new DbContextOptionsBuilder<ReceiptDbContext>().UseSqlite(connection.ToString()).Options);
    }

    internal void ExecuteFixtureSql(string sql)
    {
        using var context = OpenContext();
        context.Database.ExecuteSqlRaw(sql);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}

internal sealed record IntakeOutcome(int ExitCode, string Output, string Error)
{
    internal JsonElement Success()
    {
        Assert.True(ExitCode == 0, Error);
        Assert.Empty(Error);
        using var json = JsonDocument.Parse(Output);
        return json.RootElement.Clone();
    }

    internal void Failure(string stage, string category, int expectedCode = 1)
    {
        Assert.Equal(expectedCode, ExitCode);
        Assert.Empty(Output);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_MARKER", Error);
        Assert.DoesNotContain("synthetic-receipts.db", Error);
        using var json = JsonDocument.Parse(Error);
        Assert.Equal(new[] { "action", "category", "stage" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(stage, json.RootElement.GetProperty("stage").GetString());
        Assert.Equal(category, json.RootElement.GetProperty("category").GetString());
        Assert.NotEmpty(json.RootElement.GetProperty("action").GetString()!);
    }
}

internal sealed class TestClosureSource(WebApplication app, string origin) : IAsyncDisposable
{
    internal string Origin { get; } = origin;
    internal const string Path = "/integrations/case-closures";

    internal static async Task<TestClosureSource> StartAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(handler);
        try
        {
            await app.StartAsync();
            return new TestClosureSource(app, Assert.Single(app.Urls));
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await app.StopAsync(deadline.Token);
        await app.DisposeAsync();
    }
}
