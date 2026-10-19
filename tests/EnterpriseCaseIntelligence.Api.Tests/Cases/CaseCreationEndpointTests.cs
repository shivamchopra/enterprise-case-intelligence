using System.Net;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseCreationEndpointTests : CaseApiTest
{
    [Fact]
    public async Task CreateReturnsNormalizedNewCaseAndRetrievableLocation()
    {
        var created = await CreateAsync(" \tExample  request\r\n", "\nExample\tdescription ");

        Assert.Equal("Example  request", created.Title);
        Assert.Equal("Example\tdescription", created.Description);
        Assert.Equal("New", created.Status);
        Assert.Equal(created, await ReadAsync(created.Id));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(120, 2_000)]
    public async Task CreateAcceptsTrimmedTextBoundaries(int titleLength, int descriptionLength)
    {
        var title = new string('t', titleLength);
        var description = new string('d', descriptionLength);

        var created = await CreateAsync($" {title} ", $"\n{description}\t");

        Assert.Equal(title, created.Title);
        Assert.Equal(description, created.Description);
    }

    [Theory]
    [InlineData("{}", "title")]
    [InlineData("""{"title":null,"description":"Valid"}""", "title")]
    [InlineData("""{"title":42,"description":"Valid"}""", "title")]
    [InlineData("""{"title":false,"description":"Valid"}""", "title")]
    [InlineData("""{"title":{},"description":"Valid"}""", "title")]
    [InlineData("""{"title":"","description":"Valid"}""", "title")]
    [InlineData("""{"title":" \t\r\n","description":"Valid"}""", "title")]
    [InlineData("""{"title":"Valid"}""", "description")]
    [InlineData("""{"title":"SYNTHETIC_SENSITIVE_MARKER","description":null}""", "description")]
    [InlineData("""{"title":"Valid","description":42}""", "description")]
    [InlineData("""{"title":"Valid","description":[]}""", "description")]
    [InlineData("""{"title":"Valid","description":""}""", "description")]
    [InlineData("""{"title":"Valid","description":" \t"}""", "description")]
    [InlineData("""{"Title":"Valid","description":"Valid"}""", "title")]
    [InlineData("""{"title":"Valid","description":"Valid","id":"SYNTHETIC_SENSITIVE_MARKER"}""", "body")]
    [InlineData("""{"title":"Valid","description":"Valid","status":"New"}""", "body")]
    [InlineData("""{"title":"Valid","description":"Valid","SYNTHETIC_SENSITIVE_MARKER":true}""", "body")]
    [InlineData("""{"title":"First","title":"Second","description":"Valid"}""", "title")]
    [InlineData("""{"title":"Valid","description":"First","description":"Second"}""", "description")]
    public async Task CreateRejectsInvalidFieldsWithoutChangingExistingCase(string json, string field)
    {
        var existing = await CreateAsync();

        using var response = await SendJsonAsync(HttpMethod.Post, "/cases", json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
        Assert.Equal(existing, await ReadAsync(existing.Id));
    }

    [Theory]
    [InlineData(121, 1, "title")]
    [InlineData(1, 2_001, "description")]
    public async Task CreateRejectsOversizedText(int titleLength, int descriptionLength, string field)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = new string('t', titleLength),
            description = new string('d', descriptionLength)
        });

        using var response = await SendJsonAsync(HttpMethod.Post, "/cases", json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"title\":\"SYNTHETIC_SENSITIVE_MARKER\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"SYNTHETIC_SENSITIVE_MARKER\"")]
    [InlineData("{} {}")]
    [InlineData("""{"title":"\ud800","description":"Valid"}""")]
    [InlineData("""{"title":"\udc00","description":"Valid"}""")]
    [InlineData("""{"\ud800":true,"title":"Valid","description":"Valid"}""")]
    public async Task CreateRejectsEmptyMalformedAndNonObjectJson(string json)
    {
        using var response = await SendJsonAsync(HttpMethod.Post, "/cases", json);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", "body");
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/vnd.example+json")]
    public async Task CreateRejectsUnsupportedContentTypeBeforeParsingBody(string mediaType)
    {
        using var response = await SendJsonAsync(HttpMethod.Post, "/cases", "not-json", mediaType);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Fact]
    public async Task CreateRejectsMissingContentType()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cases")
        {
            Content = new StringContent("{}")
        };
        request.Content.Headers.ContentType = null;

        using var response = await Client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Fact]
    public async Task CreateRejectsInvalidUtf8()
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes("{\"title\":\"");
        var suffix = System.Text.Encoding.UTF8.GetBytes("\",\"description\":\"Valid\"}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cases")
        {
            Content = new ByteArrayContent([.. prefix, 0xff, .. suffix])
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await Client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", "body");
    }

    [Fact]
    public async Task CreateRejectsUnsupportedCharsetBeforeReadingBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cases") { Content = new StringContent("not-json") };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-16");

        using var response = await Client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Fact]
    public async Task CreateAcceptsQuotedUtf8Charset()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cases")
        {
            Content = new StringContent("{\"title\":\"Valid\",\"description\":\"Valid\"}")
        };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=\"utf-8\"");

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("New", (await ReadCaseAsync(response)).Status);
    }

    [Fact]
    public async Task IdenticalRequestsCreateIndependentCases()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();

        await SetStatusAsync(first.Id, "InReview");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second, await ReadAsync(second.Id));
        Assert.Equal("InReview", (await ReadAsync(first.Id)).Status);
    }
}
