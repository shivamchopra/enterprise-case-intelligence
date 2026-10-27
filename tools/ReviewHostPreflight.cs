using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EnterpriseCaseIntelligence.Tools;

// Optional .NET 10 file-based operator tool, not part of the API executable.
internal static class ReviewHostPreflight
{
    private const int OutputLimit = 128 * 1024;

#if !ECI_PREFLIGHT_TESTS
    public static Task<int> Main(string[] args) => RunAsync(args, Console.Out, RunAzureAsync);
#endif

    internal static async Task<int> RunAsync(string[] args, TextWriter output, Func<string[], Task<CommandResult>> runAzure)
    {
        // Validate before invoking any external command. The region is only a local filter.
        if (args.Length != 2 || args[0] != "--region" || args[1].Length is < 1 or > 40 ||
            !char.IsAsciiLetterLower(args[1][0]) || args[1].Any(value => !char.IsAsciiLetterLower(value) && !char.IsAsciiDigit(value)))
        {
            await WriteReportAsync(output, json =>
            {
                json.WriteString("error", "invalid_arguments");
                json.WriteString("usage", "dotnet run --file tools/ReviewHostPreflight.cs -- --region <lowercase-region-code>");
            });
            return 2;
        }

        // This complete allowlist contains only catalog reads. Never add login,
        // account switching, provider registration, deployment or create/update calls.
        var commands = new[]
        {
            new[] { "webapp", "list-runtimes", "--os", "linux", "--only-show-errors", "--output", "json" },
            new[] { "appservice", "list-locations", "--sku", "F1", "--linux-workers-enabled", "--only-show-errors", "--output", "json" }
        };
        var catalogs = await Task.WhenAll(commands.Select(async command =>
        {
            try { return await runAzure(command); }
            catch (Exception exception) when (exception is IOException or Win32Exception or OperationCanceledException or TimeoutException)
            { return new CommandResult(false, ""); }
        }));
        var runtime = ReadCatalog(catalogs[0], "config", value => value is "DOTNETCORE|10.0" or "DOTNETCORE:10.0", requireSupportedRuntime: true);
        var region = ReadCatalog(catalogs[1], "name", value => string.Equals(value.Replace(" ", ""), args[1], StringComparison.OrdinalIgnoreCase));
        var passed = runtime == "advertised" && region == "advertised";
        // No raw CLI output, errors, names, account IDs or resource metadata leave this tool.
        await WriteReportAsync(output, json =>
        {
            json.WriteString("region", args[1]);
            json.WriteBoolean("catalogChecksPassed", passed);
            json.WriteString("dotnet10LinuxRuntime", runtime);
            json.WriteString("f1LinuxRegion", region);
            json.WriteStartArray("unverified");
            foreach (var gate in new[] { "subscription_eligibility", "sql_free_limit_auto_pause", "managed_ingress", "protected_session_keys", "sql_provider", "recovery_and_operations" })
                json.WriteStringValue(gate);
            json.WriteEndArray();
        });
        // Exit 0 means both catalogs advertised candidates, never deployment approval.
        return passed ? 0 : 1;
    }

    private static async Task WriteReportAsync(TextWriter output, Action<Utf8JsonWriter> writeFields)
    {
        // Explicit fields also work with the file-based SDK's AOT analysis.
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            writeFields(json);
            json.WriteBoolean("deploymentReady", false);
            json.WriteEndObject();
        }
        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    private static string ReadCatalog(CommandResult result, string property, Func<string, bool> matches, bool requireSupportedRuntime = false)
    {
        if (!result.Success || result.Output.Length > OutputLimit) return "unavailable";
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return "unavailable";
            var recognized = document.RootElement.GetArrayLength() == 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                string? value;
                if (item.ValueKind == JsonValueKind.String) value = item.GetString();
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String)
                    value = field.GetString();
                else continue;
                recognized = true;
                if (requireSupportedRuntime && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("support", out var support) &&
                    (support.ValueKind != JsonValueKind.String || support.GetString() is not ("Active" or "Near"))) continue;
                if (value is not null && matches(value)) return "advertised";
            }
            return recognized ? "not_advertised" : "unavailable";
        }
        catch (JsonException) { return "unavailable"; }
    }

    private static async Task<CommandResult> RunAzureAsync(string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("az")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"] = "no";
        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            var exit = process.WaitForExitAsync(timeout.Token);
            // A bounded read failure must also terminate a still-running child.
            await Task.WhenAll(stdout, stderr, exit).WaitAsync(timeout.Token);
            return new(process.ExitCode == 0, process.ExitCode == 0 ? await stdout : "");
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return new(false, "");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (InvalidOperationException) { }
            }
            await timeout.CancelAsync();
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellation);
            if (count == 0) return text.ToString();
            if (text.Length + count > OutputLimit) throw new IOException("Catalog output limit exceeded.");
            text.Append(buffer, 0, count);
        }
    }

    internal sealed record CommandResult(bool Success, string Output);
}
