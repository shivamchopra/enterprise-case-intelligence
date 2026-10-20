using System.Diagnostics;
using System.IO.Pipes;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.UpgradeFixture;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseUpgradeProcessTests
{
    [Fact]
    public async Task ForcedTerminationDuringPartialBaselineWritesRecoversWholeOldFormat()
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var original = LegacyCaseStore.Initialize(fixture);
        var pipeName = "eci-" + Guid.NewGuid().ToString("N")[..16];
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var process = StartFixture("barrier", fixture.Path, pipeName);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            var ready = new byte[1];
            await pipe.ReadExactlyAsync(ready, deadline.Token);
            Assert.Equal(1, ready[0]);
            // The child has created the table and written one baseline in its uncommitted transaction.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        await output;
        Assert.DoesNotContain("Exception", await error);
        var inspected = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, inspected.ExitCode);
        Assert.Contains("Case store version: 1.", inspected.Output);
        CaseStoreUpgradeTests.AssertLegacy(fixture, original);
        fixture.Database.Upgrade();
        Assert.Equal(2, fixture.Database.Inspect());
        var store = new SqliteCaseStore(fixture.Database);
        Assert.All(original, record => Assert.Single(store.History(record.Id)!));
    }

    [Fact]
    public async Task LostUpgradeCompletionMessageIsReconciledByInspectionWithoutRepeatingUpgrade()
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var original = LegacyCaseStore.Initialize(fixture);
        using var process = StartFixture("lost-output", fixture.Path, "unused");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(1, process.ExitCode);
            Assert.Empty(await output);
            Assert.Empty(await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        var inspect = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, inspect.ExitCode);
        Assert.Contains("Case store version: 2.", inspect.Output);
        var refused = await ApiProcess.RunAsync("upgrade-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, refused.ExitCode);
        var store = new SqliteCaseStore(fixture.Database);
        Assert.All(original, record =>
        {
            Assert.Equal(record, store.Find(record.Id));
            Assert.Equal("Baseline", Assert.Single(store.History(record.Id)!).Kind);
        });
    }

    private static Process StartFixture(string mode, string path, string pipe)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(Program).Assembly.Location, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(Program).Assembly.Location, ".deps.json"));
        start.ArgumentList.Add(typeof(UpgradeFixtureProgram).Assembly.Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(pipe);
        return Process.Start(start)!;
    }
}
