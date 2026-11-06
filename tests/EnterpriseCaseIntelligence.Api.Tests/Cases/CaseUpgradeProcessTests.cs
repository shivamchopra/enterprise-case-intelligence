using System.Diagnostics;
using System.IO.Pipes;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;
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
        Assert.Equal(6, fixture.Database.Inspect());
        var store = new SqliteCaseStore(fixture.Database);
        Assert.All(original, record => Assert.Single(store.History(record.Id)!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task LostUpgradeCompletionMessageIsReconciledByInspectionWithoutRepeatingUpgrade(int version)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var original = version switch
        {
            1 => LegacyCaseStore.Initialize(fixture),
            2 => VersionTwoCaseStore.Initialize(fixture),
            3 => VersionThreeCaseStore.Initialize(fixture).Cases,
            4 => VersionFourCaseStore.Initialize(fixture).Cases,
            _ => VersionFiveCaseStore.Initialize(fixture).Cases
        };
        var history = version >= 2 ? VersionTwoCaseStore.HistoryFields(fixture) : [];
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
        Assert.Contains("Case store version: 6.", inspect.Output);
        var refused = await ApiProcess.RunAsync("upgrade-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, refused.ExitCode);
        var store = new SqliteCaseStore(fixture.Database);
        Assert.All(original, record =>
        {
            Assert.Equal(record, store.Find(record.Id));
            if (version == 1) Assert.Equal("Baseline", Assert.Single(store.History(record.Id)!).Kind);
        });
        if (version >= 2)
        {
            var actual = VersionTwoCaseStore.HistoryFields(fixture);
            Assert.Equal(history.Length, actual.Length);
            for (var index = 0; index < history.Length; index++) Assert.Equal(history[index], actual[index]);
        }
    }

    [Theory]
    [InlineData("fresh", "priority-schema")]
    [InlineData("fresh", "priority-row")]
    [InlineData("fresh", "priority-metadata")]
    [InlineData("adopted-3", "priority-schema")]
    [InlineData("adopted-3", "priority-row")]
    [InlineData("adopted-3", "priority-metadata")]
    public async Task TerminationAtGeneratedSchemaBackfillOrMetadataRecoversFormatFour(string family, string stage)
    {
        using var fixture = new TemporaryCaseDatabase(false);
        var (records, bindings) = VersionFourCaseStore.Initialize(fixture, family, 105);
        var history = VersionTwoCaseStore.HistoryFields(fixture);
        var pipeName = "eci-" + Guid.NewGuid().ToString("N")[..16];
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var process = StartFixture(stage, fixture.Path, pipeName);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            var ready = new byte[1];
            await pipe.ReadExactlyAsync(ready, deadline.Token);
            Assert.Equal(1, ready[0]);
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
        Assert.Empty(await output);
        Assert.Empty(await error);
        Assert.Equal(4, fixture.Database.Inspect());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name='case_priorities'"));
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM __EFMigrationsHistory"));
        LegacyCaseStore.AssertRecords(fixture, records);
        var actual = VersionTwoCaseStore.HistoryFields(fixture);
        Assert.Equal(history.Length, actual.Length);
        for (var index = 0; index < history.Length; index++) Assert.Equal(history[index], actual[index]);
        Assert.Equal((long)bindings.Count, fixture.Scalar("SELECT count(*) FROM case_creation_keys"));
        fixture.Database.Upgrade();
        Assert.Equal(6, fixture.Database.Inspect());
        var store = new SqliteCaseStore(fixture.Database);
        foreach (var (key, id) in bindings)
        {
            var record = records.Single(row => row.Id == id);
            Assert.Equal(record with { Status = Domain.Cases.CaseStatus.New }, store.Create(record.Title, record.Description, key).Case);
        }
    }

    [Theory]
    [InlineData("closure-schema")]
    [InlineData("closure-stream")]
    [InlineData("closure-metadata")]
    public async Task TerminationDuringClosureUpgradeRecoversFormatFive(string stage)
    {
        using var fixture = new TemporaryCaseDatabase(false);
        VersionFiveCaseStore.Initialize(fixture);
        await KillAtBarrier(stage, fixture.Path);
        Assert.Equal(5, fixture.Database.Inspect());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_schema WHERE name GLOB 'case_closure_*'"));
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM __EFMigrationsHistory"));
        fixture.Database.Upgrade();
        Assert.Equal(6, fixture.Database.Inspect());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM case_closure_notices"));
    }

    [Theory]
    [InlineData("close-status", false)]
    [InlineData("close-history", false)]
    [InlineData("close-notice", false)]
    [InlineData("close-committed", true)]
    public async Task TerminationAtClosureBoundaryRecoversPriorOrFullyCommittedState(string stage, bool committed)
    {
        using var fixture = new TemporaryCaseDatabase();
        var store = new SqliteCaseStore(fixture.Database);
        var record = store.Create("Synthetic interrupted close", "Fictional description");
        store.ChangeStatus(record.Id, CaseStatus.InReview);
        await KillAtBarrier(stage, fixture.Path, record.Id);
        Assert.Equal(6, fixture.Database.Inspect());
        var reopened = new SqliteCaseStore(fixture.Database);
        Assert.Equal(committed ? CaseStatus.Closed : CaseStatus.InReview, reopened.Find(record.Id)!.Status);
        Assert.Equal(committed ? 3 : 2, reopened.History(record.Id)!.Count);
        Assert.Equal(committed ? 1L : 0L, fixture.Scalar("SELECT count(*) FROM case_closure_notices"));
        reopened.ChangeStatus(record.Id, CaseStatus.Closed);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM case_closure_notices"));
    }

    private static async Task KillAtBarrier(string stage, string path, Guid? id = null)
    {
        var pipeName = "eci-" + Guid.NewGuid().ToString("N")[..16];
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var process = StartFixture(stage, path, pipeName, id);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            var ready = new byte[1];
            await pipe.ReadExactlyAsync(ready, deadline.Token);
            Assert.Equal(1, ready[0]);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        Assert.Empty(await output);
        Assert.Empty(await error);
    }

    private static Process StartFixture(string mode, string path, string pipe, Guid? id = null)
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
        if (id is { } caseId) start.ArgumentList.Add(caseId.ToString("D"));
        return Process.Start(start)!;
    }
}
