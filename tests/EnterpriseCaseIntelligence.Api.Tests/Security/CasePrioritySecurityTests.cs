using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Security;

public class CasePrioritySecurityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PriorityRoleCsrfExpiryRevisionAndSignoutHoldAcrossIngressAndCertificateSources(bool proxy, bool configurationCertificate)
    {
        await using var host = new SecurityHost();
        host.Start(proxy: proxy,
            requestSetup: proxy ? context => { context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Request.Scheme = "http"; }
        : null,
            configurationOverrides: configurationCertificate ? host.ConfigurationCertificate() : null);
        HttpClient Client(string ticket)
        {
            var client = host.CreateClient(ticket);
            if (proxy) client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            return client;
        }
        var store = new SqliteCaseStore(host.Database.Database);
        var record = store.Create("Synthetic priority security", "Fictional content");
        var path = $"/cases/{record.Id:D}/priority";
        using var worker = Client(host.Ticket("Caseworker"));
        using var reader = Client(host.Ticket("Reader"));
        await CaseSecurityTests.Csrf(reader);
        using (var deniedNoop = await reader.PutAsJsonAsync(path, new { priority = "Normal" })) Assert.Equal(HttpStatusCode.Forbidden, deniedNoop.StatusCode);
        using (var deniedChange = await reader.PutAsJsonAsync(path, new { priority = "High" })) Assert.Equal(HttpStatusCode.Forbidden, deniedChange.StatusCode);
        using (var missingCsrf = await worker.PutAsJsonAsync(path, new { priority = "Normal" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            using var error = JsonDocument.Parse(await missingCsrf.Content.ReadAsStringAsync());
            Assert.True(error.RootElement.GetProperty("errors").TryGetProperty("csrf", out _));
        }
        await CaseSecurityTests.Csrf(worker);
        foreach (var value in new[] { "High", "High" })
        {
            using var write = await worker.PutAsJsonAsync(path, new { priority = value });
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }
        using (var read = await reader.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.True(read.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal("High", body.RootElement.GetProperty("priority").GetString());
        }
        using (var filtered = await reader.GetAsync("/cases?priority=High")) Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        using (var logout = await worker.PostAsync("/auth/logout", null))
        {
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
            Assert.Contains(logout.Headers.GetValues("Set-Cookie"), value => value.StartsWith(SecurityHost.CookieName + "=;", StringComparison.Ordinal));
        }
        // This fixture injects its ticket through an explicit Cookie header. Apply the
        // cookie deletion a browser performs; logout does not revoke copied tickets.
        worker.DefaultRequestHeaders.Remove("Cookie");
        using (var signedOut = await worker.GetAsync(path)) Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        host.Clock.Now = host.Clock.Now.AddMinutes(31);
        using (var expired = await reader.GetAsync(path)) Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(new(record.Id, CasePriority.High), store.Priority(record.Id));
        Assert.Single(store.History(record.Id)!);
        var ticket = host.Ticket("Caseworker");
        await host.Factory.DisposeAsync();
        host.Start(revision: "priority-revoked", proxy: proxy,
            requestSetup: proxy ? context => { context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Request.Scheme = "http"; }
        : null,
            configurationOverrides: configurationCertificate ? host.ConfigurationCertificate() : null);
        using var revoked = Client(ticket);
        using var rejected = await revoked.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }
}
