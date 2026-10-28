using System.Diagnostics;
using System.Text;

namespace EnterpriseCaseIntelligence.Api.Tests;

internal sealed class ApiProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly TaskCompletionSource<Uri> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Uri Address { get; private set; } = null!;

    private ApiProcess(IReadOnlyDictionary<string, string?>? environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        // Browser CI runs the published executable from an unrelated working
        // directory to verify asset publication rather than source-tree lookup.
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("ECI_TEST_API_ASSEMBLY") ?? typeof(Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var setting in environment)
        {
            if (setting.Value is null) start.Environment.Remove(setting.Key);
            else start.Environment[setting.Key] = setting.Value;
        }
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => Record(args.Data);
        _process.ErrorDataReceived += (_, args) => Record(args.Data);
        _process.Exited += (_, _) => _listening.TrySetException(new InvalidOperationException("API exited before readiness."));
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void Record(string? line)
    {
        if (line is null) return;
        lock (_output) _output.AppendLine(line);
        const string marker = "Now listening on: ";
        var position = line.IndexOf(marker, StringComparison.Ordinal);
        if (position >= 0 && Uri.TryCreate(line[(position + marker.Length)..], UriKind.Absolute, out var address))
        {
            _listening.TrySetResult(address);
        }
    }

    public static async Task<ApiProcess> StartAsync(string path)
    {
        var process = new ApiProcess(null, "--CaseStorage:DatabasePath", path, "--urls", "http://127.0.0.1:0",
            "--Security:Mode", "LocalDemo",
            "--Logging:LogLevel:Default", "Warning", "--Logging:LogLevel:Microsoft.Hosting.Lifetime", "Information");
        try
        {
            process.Address = await process._listening.Task.WaitAsync(TimeSpan.FromSeconds(25));
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(params string[] arguments)
        => await RunRawAsync(arguments.FirstOrDefault() is "init-store" or "inspect-store" or "upgrade-store" ? arguments :
            [.. arguments, "--Security:Mode", "LocalDemo", "--urls", "http://127.0.0.1:0"]);

    public static Task<(int ExitCode, string Output)> RunRawAsync(params string[] arguments)
        => RunWithEnvironmentAsync(null, arguments);

    public static async Task<(int ExitCode, string Output)> RunWithEnvironmentAsync(IReadOnlyDictionary<string, string?>? environment, params string[] arguments)
    {
        using var process = new ApiProcess(environment, arguments);
        await process._process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        process._process.WaitForExit(); // Drain redirected output events before asserting diagnostics.
        lock (process._output) return (process._process.ExitCode, process._output.ToString());
    }

    public async Task KillAsync()
    {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }

        _process.Dispose();
    }
}
