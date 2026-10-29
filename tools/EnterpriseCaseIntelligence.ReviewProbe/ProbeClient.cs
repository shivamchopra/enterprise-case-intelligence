using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EnterpriseCaseIntelligence.ReviewProbe;

internal static class ProbeClient
{
    internal static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    public static HttpClient CreateClient() => new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<string> Send(IConfiguration configuration, string? receipt, HttpClient client)
    {
        var origin = ProbeSettings.ReadOrigin(configuration["Probe:PublicOrigin"]);
        var credential = ProbeSettings.ReadCredential(configuration);
        if (receipt is not null && !ProbeEndpoint.ReceiptSyntax(receipt)) throw new ArgumentException();
        using var request = new HttpRequestMessage(receipt is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri(origin, receipt is null ? "/probe" : "/probe/verify"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (receipt is not null) { request.Content = new StringContent(receipt, Encoding.ASCII); request.Content.Headers.ContentType = new("text/plain"); }
        // The owned production client validates normal TLS, follows no redirects,
        // retains no cookies, and has a finite whole-operation deadline.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > 32 * 1024) throw new HttpRequestException();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[32 * 1024 + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token);
            if (read == 0) break;
            count += read;
        }
        if (count > 32 * 1024) throw new HttpRequestException();
        using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new() { MaxDepth = 8 });
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1 || document.RootElement.GetProperty("deploymentReady").GetBoolean()) throw new HttpRequestException();
        return document.RootElement.GetRawText();
    }
}
