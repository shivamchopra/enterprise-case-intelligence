using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseKeyProtocolTests
{
    [Fact]
    public async Task ActualHttpRejectsPresentEmptyAndRepeatedHeadersWhileMissingHeaderRemainsKeyless()
    {
        using var fixture = new TemporaryCaseDatabase();
        using var process = await ApiProcess.StartAsync(fixture.Path);
        foreach (var headers in new[]
        {
            "Idempotency-Key:\r\n", "Idempotency-Key: \t \r\n",
            "Idempotency-Key: \"b6ab3cd4-87cf-4bdd-ae55-103ea9978018\"\r\nIdempotency-Key: \"b6ab3cd4-87cf-4bdd-ae55-103ea9978018\"\r\n"
        })
        {
            var response = await Exchange(process.Address, headers);
            Assert.StartsWith("HTTP/1.1 400", response);
            using var body = JsonDocument.Parse(response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
            Assert.Equal("validation_failed", body.RootElement.GetProperty("code").GetString());
            Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("idempotencyKey", out _));
        }
        Assert.Equal(0, fixture.Count());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM case_creation_keys"));
        Assert.StartsWith("HTTP/1.1 201", await Exchange(process.Address, ""));
        Assert.Equal(1, fixture.Count());
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    private static async Task<string> Exchange(Uri address, string headers)
    {
        const string body = "{\"title\":\"Synthetic wire test\",\"description\":\"Synthetic\"}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, deadline.Token);
        using var stream = socket.GetStream();
        var request = $"POST /cases HTTP/1.0\r\nHost: {address.Host}:{address.Port}\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n{headers}\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), deadline.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(deadline.Token);
    }
}
