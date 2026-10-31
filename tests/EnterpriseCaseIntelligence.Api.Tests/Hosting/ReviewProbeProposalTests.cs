using System.Text.Json;
using System.Text.Json.Nodes;
using EnterpriseCaseIntelligence.Tools;

namespace EnterpriseCaseIntelligence.Api.Tests.Hosting;

public sealed class ReviewProbeProposalTests
{
    private static JsonObject Proposal() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Hosting", "review-probe.azuredeploy.json")))!.AsObject();

    [Fact]
    public async Task CheckedInEnvelopePassesOfflineWithoutAzureOrDeploymentApproval()
    {
        var (exit, report) = await Check(Proposal().ToJsonString());
        using var json = JsonDocument.Parse(report);
        Assert.Equal(0, exit);
        Assert.True(json.RootElement.GetProperty("proposalConstraintsPassed").GetBoolean());
        Assert.False(json.RootElement.GetProperty("deploymentReady").GetBoolean());
        Assert.Contains("probe_application", report);
        Assert.Contains("operator_approval", report);
        Assert.Contains("credential_delivery", report);
    }

    [Theory]
    [InlineData("paid-sku")]
    [InlineData("parameterized-sku")]
    [InlineData("extra-resource")]
    [InlineData("nested-resource")]
    [InlineData("external-plan")]
    [InlineData("always-on")]
    [InlineData("http-allowed")]
    [InlineData("weak-site-tls")]
    [InlineData("weak-scm-tls")]
    [InlineData("basic-publishing")]
    [InlineData("automatic-forwarding")]
    [InlineData("request-logging")]
    [InlineData("extra-app-setting")]
    [InlineData("secret-parameter")]
    [InlineData("secret-output")]
    [InlineData("resource-loop")]
    [InlineData("missing-startup")]
    [InlineData("implicit-startup")]
    [InlineData("normal-serving")]
    [InlineData("startup-initialization")]
    [InlineData("startup-shell-command")]
    [InlineData("case-api-startup")]
    public async Task PaidOrUnreviewedCapabilitiesAndWeakenedBoundariesAreRejected(string mutation)
    {
        var proposal = Proposal();
        var resources = proposal["resources"]!.AsArray();
        var plan = resources[0]!;
        var site = resources[1]!["properties"]!;
        var config = site["siteConfig"]!;
        switch (mutation)
        {
            case "paid-sku": plan["sku"]!["name"] = "B1"; break;
            case "parameterized-sku": plan["sku"]!["name"] = "[parameters('PRIVATE_SKU')]"; break;
            case "extra-resource": resources.Add(new JsonObject { ["type"] = "Microsoft.KeyVault/vaults", ["name"] = "PRIVATE_RESOURCE" }); break;
            case "nested-resource": plan["resources"] = new JsonArray(new JsonObject { ["type"] = "PRIVATE_NESTED_RESOURCE" }); break;
            case "external-plan": site["serverFarmId"] = "PRIVATE_EXISTING_PAID_PLAN"; break;
            case "always-on": config["alwaysOn"] = true; break;
            case "http-allowed": site["httpsOnly"] = false; break;
            case "weak-site-tls": config["minTlsVersion"] = "1.0"; break;
            case "weak-scm-tls": config["scmMinTlsVersion"] = "1.0"; break;
            case "basic-publishing": resources[2]!["properties"]!["allow"] = true; break;
            case "automatic-forwarding": config["appSettings"]![0]!["value"] = "true"; break;
            case "request-logging": config["httpLoggingEnabled"] = true; break;
            case "extra-app-setting": config["appSettings"]!.AsArray().Add(new JsonObject { ["name"] = "Security__CertificatePfx", ["value"] = "PRIVATE_PFX" }); break;
            case "secret-parameter": proposal["parameters"]!["password"] = new JsonObject { ["type"] = "secureString", ["defaultValue"] = "PRIVATE_PASSWORD" }; break;
            case "secret-output": proposal["outputs"]!["private"] = new JsonObject { ["type"] = "string", ["value"] = "PRIVATE_OUTPUT" }; break;
            case "resource-loop": plan["copy"] = new JsonObject { ["name"] = "PRIVATE_LOOP", ["count"] = 10 }; break;
            case "missing-startup": config.AsObject().Remove("appCommandLine"); break;
            case "implicit-startup": config["appCommandLine"] = "dotnet /home/site/wwwroot/EnterpriseCaseIntelligence.ReviewProbe.dll"; break;
            case "normal-serving": config["appCommandLine"] = "dotnet /home/site/wwwroot/EnterpriseCaseIntelligence.ReviewProbe.dll serve"; break;
            case "startup-initialization": config["appCommandLine"] = "dotnet /home/site/wwwroot/EnterpriseCaseIntelligence.ReviewProbe.dll init-keys"; break;
            case "startup-shell-command": config["appCommandLine"] = "dotnet /home/site/wwwroot/EnterpriseCaseIntelligence.ReviewProbe.dll prepare; PRIVATE_COMMAND"; break;
            case "case-api-startup": config["appCommandLine"] = "dotnet /home/site/wwwroot/EnterpriseCaseIntelligence.Api.dll"; break;
        }
        var (exit, report) = await Check(proposal.ToJsonString());
        Assert.Equal(1, exit);
        Assert.DoesNotContain("PRIVATE_", report);
        using var json = JsonDocument.Parse(report);
        Assert.False(json.RootElement.GetProperty("proposalConstraintsPassed").GetBoolean());
        Assert.False(json.RootElement.GetProperty("deploymentReady").GetBoolean());
    }

    [Theory]
    [InlineData("PRIVATE_MALFORMED_DOCUMENT")]
    [InlineData("{\"PRIVATE_UNKNOWN_PROPERTY\":true}")]
    [InlineData("[]")]
    public async Task MalformedOrUnrecognizedDocumentsHaveSafeOutput(string content)
    {
        var (exit, report) = await Check(content);
        Assert.Equal(1, exit);
        Assert.DoesNotContain("PRIVATE_", report);
        Assert.DoesNotContain("Exception", report);
    }

    [Fact]
    public async Task OversizedAndMissingDocumentsCannotPassOrExposePaths()
    {
        var (exit, report) = await Check(new string('x', 64 * 1024 + 1));
        Assert.Equal(1, exit);
        Assert.True(report.Length < 1000);
        using var output = new StringWriter();
        exit = await ReviewHostPreflight.RunAsync(["--proposal", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "PRIVATE_PATH.json")], output,
            _ => throw new InvalidOperationException("Azure must not run for an offline proposal."));
        Assert.Equal(1, exit);
        Assert.DoesNotContain("PRIVATE_PATH", output.ToString());
    }

    private static async Task<(int Exit, string Report)> Check(string content)
    {
        var directory = Directory.CreateTempSubdirectory("eci-proposal-");
        try
        {
            var path = Path.Combine(directory.FullName, "PRIVATE_PROPOSAL.json");
            await File.WriteAllTextAsync(path, content);
            var calls = 0;
            using var output = new StringWriter();
            var exit = await ReviewHostPreflight.RunAsync(["--proposal", path], output, _ =>
            {
                calls++;
                return Task.FromResult(new ReviewHostPreflight.CommandResult(true, "PRIVATE_AZURE_OUTPUT"));
            });
            Assert.Equal(0, calls);
            Assert.DoesNotContain(directory.FullName, output.ToString());
            return (exit, output.ToString());
        }
        finally { directory.Delete(recursive: true); }
    }
}
