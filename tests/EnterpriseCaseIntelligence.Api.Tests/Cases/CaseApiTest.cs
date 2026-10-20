using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public abstract class CaseApiTest : IDisposable
{
    protected const string ValidTitle = "Correct the sample guide";
    protected const string ValidDescription = "Update the fictional example section.";
    protected TemporaryCaseDatabase Database { get; } = new();
    protected WebApplicationFactory<Program> Factory { get; }
    protected HttpClient Client { get; }

    protected CaseApiTest(TimeProvider? clock = null)
    {
        Factory = Database.CreateFactory(clock);
        Client = Factory.CreateClient();
    }

    protected async Task<CaseRepresentation> CreateAsync(string title = ValidTitle, string description = ValidDescription)
    {
        using var response = await Client.PostAsJsonAsync("/cases", new { title, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await ReadCaseAsync(response);
        Assert.Equal(PathFor(result.Id), response.Headers.Location?.OriginalString);
        return result;
    }

    protected async Task<CaseRepresentation> ReadAsync(Guid id)
    {
        using var response = await Client.GetAsync(PathFor(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCaseAsync(response);
    }

    protected async Task<CaseRepresentation> SetStatusAsync(Guid id, string status)
    {
        using var response = await Client.PutAsJsonAsync(PathFor(id) + "/status", new { status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadCaseAsync(response);
    }

    protected async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string json, string mediaType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType)
        };
        return await Client.SendAsync(request);
    }

    protected static string PathFor(Guid id) => $"/cases/{id:D}";

    protected CaseHistoryEntry[] StoredHistory(Guid id) => new SqliteCaseStore(Database.Database).History(id)!.ToArray();

    protected static async Task<CaseRepresentation> ReadCaseAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ReadCase(document.RootElement);
    }

    protected static CaseRepresentation ReadCase(JsonElement body)
    {
        Assert.Equal(new[] { "description", "id", "status", "title" },
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var id = body.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id.ToString("D"), body.GetProperty("id").GetString());
        return new(id, body.GetProperty("title").GetString()!, body.GetProperty("description").GetString()!,
            body.GetProperty("status").GetString()!);
    }

    protected static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string code, string? field = null)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var body = document.RootElement;
        Assert.Equal("about:blank", body.GetProperty("type").GetString());
        Assert.Equal((int)expectedStatus, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        var expectedTitle = expectedStatus switch
        {
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.NotFound => "Not Found",
            HttpStatusCode.Conflict => "Conflict",
            HttpStatusCode.UnsupportedMediaType => "Unsupported Media Type",
            HttpStatusCode.InternalServerError => "Internal Server Error",
            _ => throw new ArgumentOutOfRangeException(nameof(expectedStatus))
        };
        Assert.Equal(expectedTitle, body.GetProperty("title").GetString());
        if (field is not null)
        {
            Assert.NotEmpty(body.GetProperty("errors").GetProperty(field).EnumerateArray());
        }

        Assert.DoesNotContain("SYNTHETIC_SENSITIVE_MARKER", json);
        Assert.DoesNotContain(ValidDescription, json);
        Assert.False(body.TryGetProperty("traceId", out _));
        Assert.False(body.TryGetProperty("stackTrace", out _));
        Assert.False(body.TryGetProperty("exception", out _));
        Assert.False(body.TryGetProperty("environment", out _));
        Assert.Null(response.Headers.Location);
    }

    protected sealed record CaseRepresentation(Guid Id, string Title, string Description, string Status);

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        Database.Dispose();
        GC.SuppressFinalize(this);
    }
}
