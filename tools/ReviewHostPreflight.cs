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
        if (args.Length == 2 && args[0] == "--proposal") return await CheckProposalAsync(args[1], output);
        // Validate before invoking any external command. The region is only a local filter.
        if (args.Length != 2 || args[0] != "--region" || args[1].Length is < 1 or > 40 ||
            !char.IsAsciiLetterLower(args[1][0]) || args[1].Any(value => !char.IsAsciiLetterLower(value) && !char.IsAsciiDigit(value)))
        {
            await WriteReportAsync(output, json =>
            {
                json.WriteString("error", "invalid_arguments");
                json.WriteString("usage", "dotnet run --file tools/ReviewHostPreflight.cs -- --region <lowercase-region-code> OR --proposal <template-path>");
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

    private static async Task<int> CheckProposalAsync(string path, TextWriter output)
    {
        var passed = false;
        try
        {
            // A proposal can contain private values. Bound the read and emit no
            // raw document, path, resource name or parser/IO diagnostic.
            await using var file = File.OpenRead(path);
            var buffer = new byte[64 * 1024 + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await file.ReadAsync(buffer.AsMemory(length));
                if (count == 0) break;
                length += count;
            }
            if (length <= 64 * 1024)
            {
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 16 });
                passed = IsProbeProposal(document.RootElement);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { }
        await WriteReportAsync(output, json =>
        {
            json.WriteBoolean("proposalConstraintsPassed", passed);
            json.WriteString("scope", "offline_f1_probe_envelope_only");
            json.WriteStartArray("unverified");
            foreach (var gate in new[] { "azure_resource_validation", "subscription_eligibility", "operator_approval", "probe_application", "backend_isolation", "header_behavior", "credential_delivery", "key_retention" })
                json.WriteStringValue(gate);
            json.WriteEndArray();
        });
        return passed ? 0 : 1;
    }

    private static bool IsProbeProposal(JsonElement root)
    {
        if (!Fields(root, "$schema", "contentVersion", "parameters", "resources", "outputs") ||
            Text(root, "$schema") != "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#" ||
            Text(root, "contentVersion") != "1.0.0.0" || !Fields(root.GetProperty("outputs")) ||
            !Fields(root.GetProperty("parameters"), "location", "planName", "siteName")) return false;
        foreach (var parameter in root.GetProperty("parameters").EnumerateObject())
        {
            if (!Fields(parameter.Value, "type", "minLength", "maxLength") || Text(parameter.Value, "type") != "string" ||
                !Number(parameter.Value, "minLength", parameter.Name == "location" ? 1 : 3) || !Number(parameter.Value, "maxLength", 40)) return false;
        }
        var resources = root.GetProperty("resources");
        if (resources.ValueKind != JsonValueKind.Array || resources.GetArrayLength() != 4) return false;
        JsonElement? plan = null, site = null;
        var policies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources.EnumerateArray())
        {
            if (Text(resource, "apiVersion") != "2024-11-01") return false;
            switch (Text(resource, "type"))
            {
                case "Microsoft.Web/serverfarms": if (plan is not null) return false; plan = resource; break;
                case "Microsoft.Web/sites": if (site is not null) return false; site = resource; break;
                case "Microsoft.Web/sites/basicPublishingCredentialsPolicies":
                    if (!Fields(resource, "type", "apiVersion", "name", "dependsOn", "properties") ||
                        !Fields(resource.GetProperty("properties"), "allow") || !Flag(resource.GetProperty("properties"), "allow", false) ||
                        !OneText(resource.GetProperty("dependsOn"), "[resourceId('Microsoft.Web/sites', parameters('siteName'))]")) return false;
                    var name = Text(resource, "name");
                    if (name is not ("[concat(parameters('siteName'), '/scm')]" or "[concat(parameters('siteName'), '/ftp')]") || !policies.Add(name)) return false;
                    break;
                default: return false;
            }
        }
        if (plan is not { } farm || site is not { } web || policies.Count != 2) return false;
        if (!Fields(farm, "type", "apiVersion", "name", "location", "kind", "sku", "properties") ||
            Text(farm, "name") != "[parameters('planName')]" || Text(farm, "location") != "[parameters('location')]" || Text(farm, "kind") != "linux" ||
            !Fields(farm.GetProperty("sku"), "name", "tier", "capacity") || Text(farm.GetProperty("sku"), "name") != "F1" ||
            Text(farm.GetProperty("sku"), "tier") != "Free" || !Number(farm.GetProperty("sku"), "capacity", 1) ||
            !Fields(farm.GetProperty("properties"), "reserved", "perSiteScaling") ||
            !Flag(farm.GetProperty("properties"), "reserved", true) || !Flag(farm.GetProperty("properties"), "perSiteScaling", false)) return false;
        const string farmId = "[resourceId('Microsoft.Web/serverfarms', parameters('planName'))]";
        if (!Fields(web, "type", "apiVersion", "name", "location", "kind", "dependsOn", "properties") ||
            Text(web, "name") != "[parameters('siteName')]" || Text(web, "location") != "[parameters('location')]" || Text(web, "kind") != "app,linux" ||
            !OneText(web.GetProperty("dependsOn"), farmId)) return false;
        var properties = web.GetProperty("properties");
        if (!Fields(properties, "serverFarmId", "httpsOnly", "clientAffinityEnabled", "siteConfig") || Text(properties, "serverFarmId") != farmId ||
            !Flag(properties, "httpsOnly", true) || !Flag(properties, "clientAffinityEnabled", false)) return false;
        var config = properties.GetProperty("siteConfig");
        if (!Fields(config, "linuxFxVersion", "alwaysOn", "minTlsVersion", "scmMinTlsVersion", "ftpsState", "appSettings",
                "remoteDebuggingEnabled", "httpLoggingEnabled", "detailedErrorLoggingEnabled", "requestTracingEnabled") ||
            Text(config, "linuxFxVersion") != "DOTNETCORE|10.0" || !Flag(config, "alwaysOn", false) ||
            !Flag(config, "remoteDebuggingEnabled", false) || !Flag(config, "httpLoggingEnabled", false) ||
            !Flag(config, "detailedErrorLoggingEnabled", false) || !Flag(config, "requestTracingEnabled", false) ||
            Text(config, "minTlsVersion") != "1.2" || Text(config, "scmMinTlsVersion") != "1.2" || Text(config, "ftpsState") != "Disabled") return false;
        var settings = config.GetProperty("appSettings");
        if (settings.ValueKind != JsonValueKind.Array || settings.GetArrayLength() != 2) return false;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "false", ["WEBSITES_ENABLE_APP_SERVICE_STORAGE"] = "true" };
        foreach (var setting in settings.EnumerateArray())
        {
            var name = Text(setting, "name");
            if (!Fields(setting, "name", "value") || name is null || !expected.Remove(name, out var value) || Text(setting, "value") != value) return false;
        }
        return expected.Count == 0;
    }

    private static bool Fields(JsonElement value, params string[] names)
        => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal));
    private static string? Text(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool Flag(JsonElement value, string name, bool expected)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
    private static bool Number(JsonElement value, string name, int expected)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.TryGetInt32(out var number) && number == expected;
    private static bool OneText(JsonElement value, string expected)
        => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 && value[0].ValueKind == JsonValueKind.String && value[0].GetString() == expected;

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
