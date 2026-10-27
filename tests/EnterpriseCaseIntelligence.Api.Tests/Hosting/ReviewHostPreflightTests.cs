using System.Collections.Concurrent;
using System.Text.Json;
using EnterpriseCaseIntelligence.Tools;

namespace EnterpriseCaseIntelligence.Api.Tests.Hosting;

public sealed class ReviewHostPreflightTests
{
    [Theory]
    [InlineData("[\"DOTNETCORE|10.0\"]", "[\"East US\"]")]
    [InlineData("[\"DOTNETCORE:10.0\"]", "[{\"name\":\"eastus\"}]")]
    [InlineData("[{\"config\":\"DOTNETCORE|10.0\",\"support\":\"Active\"}]", "[{\"name\":\"East US\"}]")]
    public async Task CatalogFormatsCanPassButNeverEstablishDeploymentReadiness(string runtimes, string regions)
    {
        var (code, report, calls) = await RunAsync(runtimes, regions);
        using var document = JsonDocument.Parse(report);
        Assert.Equal(0, code);
        Assert.True(document.RootElement.GetProperty("catalogChecksPassed").GetBoolean());
        Assert.False(document.RootElement.GetProperty("deploymentReady").GetBoolean());
        Assert.Contains("subscription_eligibility", report);
        Assert.Contains("protected_session_keys", report);
        Assert.Equal(2, calls.Length);
        Assert.Contains(calls, call => call.SequenceEqual(new[] { "webapp", "list-runtimes", "--os", "linux", "--only-show-errors", "--output", "json" }));
        Assert.Contains(calls, call => call.SequenceEqual(new[] { "appservice", "list-locations", "--sku", "F1", "--linux-workers-enabled", "--only-show-errors", "--output", "json" }));
    }

    [Theory]
    [InlineData("[]", "[{\"name\":\"East US\"}]", "not_advertised", "advertised")]
    [InlineData("[\"DOTNETCORE|100.0\",\"DOTNETCORE|9.0\"]", "[\"East US\"]", "not_advertised", "advertised")]
    [InlineData("[{\"config\":\"DOTNETCORE|10.0\",\"support\":\"Removed\"}]", "[\"East US\"]", "not_advertised", "advertised")]
    [InlineData("[\"DOTNETCORE|10.0\"]", "[\"East US 2\"]", "advertised", "not_advertised")]
    [InlineData("{\"error\":\"PRIVATE_PROVIDER_MARKER\"}", "[]", "unavailable", "not_advertised")]
    [InlineData("invalid PRIVATE_PROVIDER_MARKER", "[{}]", "unavailable", "unavailable")]
    public async Task MissingOrMalformedCatalogsCannotPass(string runtimes, string regions, string runtimeStatus, string regionStatus)
    {
        var (code, report, _) = await RunAsync(runtimes, regions);
        using var document = JsonDocument.Parse(report);
        Assert.Equal(1, code);
        Assert.Equal(runtimeStatus, document.RootElement.GetProperty("dotnet10LinuxRuntime").GetString());
        Assert.Equal(regionStatus, document.RootElement.GetProperty("f1LinuxRegion").GetString());
        Assert.False(document.RootElement.GetProperty("deploymentReady").GetBoolean());
        Assert.DoesNotContain("PRIVATE_PROVIDER_MARKER", report);
    }

    [Fact]
    public async Task CliFailureAndUnrecognizedPrivateMetadataNeverAppearInTheReport()
    {
        const string metadata = "[{\"config\":\"DOTNETCORE|10.0\",\"tenantId\":\"PRIVATE_TENANT_MARKER\",\"clientSecret\":\"PRIVATE_SECRET_MARKER\"}]";
        var (code, report, _) = await RunAsync(metadata, "PRIVATE_ACCOUNT_ERROR", regionCommandSucceeds: false);
        Assert.Equal(1, code);
        Assert.DoesNotContain("PRIVATE_", report);
        Assert.Contains("unavailable", report);
    }

    [Fact]
    public async Task OversizedCatalogIsUnavailableAndNotEchoed()
    {
        var (code, report, _) = await RunAsync(new string('x', 128 * 1024 + 1), "[]");
        Assert.Equal(1, code);
        Assert.Contains("unavailable", report);
        Assert.True(report.Length < 1000);
    }

    [Theory]
    [InlineData("East US")]
    [InlineData("eastus;PRIVATE_MARKER")]
    [InlineData("https://PRIVATE_MARKER")]
    [InlineData("eastus\n")]
    [InlineData("")]
    public async Task InvalidRegionNeverInvokesAzureOrEchoesInput(string region)
    {
        using var output = new StringWriter();
        var called = false;
        var code = await ReviewHostPreflight.RunAsync(["--region", region], output, _ =>
        {
            called = true;
            return Task.FromResult(new ReviewHostPreflight.CommandResult(true, "[]"));
        });
        Assert.Equal(2, code);
        Assert.False(called);
        Assert.DoesNotContain("PRIVATE_MARKER", output.ToString());
        Assert.Contains("invalid_arguments", output.ToString());
    }

    [Fact]
    public async Task MissingCliIsReportedWithoutPathsOrRawExceptionDetails()
    {
        using var output = new StringWriter();
        var code = await ReviewHostPreflight.RunAsync(["--region", "eastus"], output,
            _ => throw new System.ComponentModel.Win32Exception("PRIVATE_EXECUTABLE_PATH"));
        Assert.Equal(1, code);
        Assert.DoesNotContain("PRIVATE_EXECUTABLE_PATH", output.ToString());
        Assert.Contains("unavailable", output.ToString());
    }

    private static async Task<(int Code, string Report, string[][] Calls)> RunAsync(string runtimes, string regions, bool regionCommandSucceeds = true)
    {
        var calls = new ConcurrentBag<string[]>();
        using var output = new StringWriter();
        var code = await ReviewHostPreflight.RunAsync(["--region", "eastus"], output, arguments =>
        {
            calls.Add(arguments);
            return Task.FromResult(arguments[0] == "webapp"
                ? new ReviewHostPreflight.CommandResult(true, runtimes)
                : new ReviewHostPreflight.CommandResult(regionCommandSucceeds, regions));
        });
        return (code, output.ToString(), calls.ToArray());
    }
}
