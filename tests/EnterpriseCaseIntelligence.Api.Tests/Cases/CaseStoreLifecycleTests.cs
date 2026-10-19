using EnterpriseCaseIntelligence.Api.Cases;
using Xunit.Abstractions;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseStoreLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MissingConfigurationExitsWithSafeDiagnostic()
    {
        var result = await ApiProcess.RunAsync("--CaseStorage:DatabasePath", "");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Case store unavailable or incompatible", result.Output);
        Assert.DoesNotContain("Now listening on:", result.Output);
        Assert.DoesNotContain("Exception", result.Output);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.db")]
    [InlineData(":memory:")]
    [InlineData("file:/tmp/example.db")]
    public void InvalidConfigurationIsRejected(string? path)
    {
        Assert.Throws<ArgumentException>(() => new CaseDatabase(path));
    }

    [Fact]
    public async Task InitializationIsExplicitAndNeverOverwritesEvenAnEmptyExistingFile()
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        var missing = await ApiProcess.RunAsync("--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, missing.ExitCode);
        Assert.False(File.Exists(fixture.Path));

        File.WriteAllBytes(fixture.Path, []);
        var refused = await ApiProcess.RunAsync("init-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, refused.ExitCode);
        Assert.Empty(File.ReadAllBytes(fixture.Path));
        File.Delete(fixture.Path);
        var initialized = await ApiProcess.RunAsync("init-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(0, initialized.ExitCode);
        Assert.DoesNotContain("Now listening on:", initialized.Output);
        var before = File.ReadAllBytes(fixture.Path);
        var repeated = await ApiProcess.RunAsync("init-store", "--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, repeated.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        fixture.Database.ValidateStartup();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("corrupt")]
    [InlineData("version")]
    [InlineData("schema")]
    [InlineData("constraints")]
    [InlineData("wal")]
    [InlineData("unwritable-journal")]
    public async Task StartupRefusesUnsupportedOrUnwritableStoreWithoutReset(string condition)
    {
        using var fixture = new TemporaryCaseDatabase(initialize: false);
        switch (condition)
        {
            case "empty": File.WriteAllBytes(fixture.Path, []); break;
            case "corrupt": File.WriteAllText(fixture.Path, "SYNTHETIC_SENSITIVE_MARKER"); break;
            case "version": fixture.Database.Initialize(); fixture.Execute("PRAGMA user_version=2"); break;
            case "schema": fixture.Database.Initialize(); fixture.Execute("ALTER TABLE cases ADD COLUMN extra TEXT"); break;
            case "constraints":
                fixture.Database.Initialize();
                fixture.Execute("DROP TABLE cases; CREATE TABLE cases (id TEXT PRIMARY KEY, title TEXT, description TEXT, status TEXT)");
                break;
            case "wal": fixture.Database.Initialize(); fixture.Execute("PRAGMA journal_mode=WAL"); break;
            case "unwritable-journal":
                fixture.Database.Initialize();
                Directory.CreateDirectory(fixture.Path + "-journal");
                break;
        }

        var before = File.Exists(fixture.Path) ? File.ReadAllBytes(fixture.Path) : null;
        var result = await ApiProcess.RunAsync("--CaseStorage:DatabasePath", fixture.Path);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Case store unavailable or incompatible", result.Output);
        Assert.DoesNotContain("Now listening on:", result.Output);
        Assert.DoesNotContain(fixture.Path, result.Output);
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", result.Output);
        if (before is null) Assert.False(File.Exists(fixture.Path));
        else Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void StartupCheckDoesNotChangeCommittedStateAndReportsBundledEngineVersion()
    {
        using var fixture = new TemporaryCaseDatabase();
        var before = File.ReadAllBytes(fixture.Path);
        fixture.Database.ValidateStartup();
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Equal(1L, fixture.Scalar("PRAGMA user_version"));
        Assert.Equal("delete", fixture.Scalar("PRAGMA journal_mode"));
        Assert.Equal(2L, fixture.Scalar("PRAGMA synchronous"));
        var version = Version.Parse((string)fixture.Scalar("SELECT sqlite_version()")!);
        output.WriteLine("Bundled SQLite engine: {0}", version);
        Assert.True(version >= new Version(3, 37, 0), version.ToString());
    }
}
