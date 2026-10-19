using System.Net;
using System.Text.Json;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseListValidationTests : CaseListApiTest
{
    [Theory]
    [InlineData("status=", "status")]
    [InlineData("status", "status")]
    [InlineData("status=all", "status")]
    [InlineData("status=new", "status")]
    [InlineData("status=NEW", "status")]
    [InlineData("status=%20New", "status")]
    [InlineData("status=New%00", "status")]
    [InlineData("status=SYNTHETIC_SENSITIVE_MARKER", "status")]
    [InlineData("status=New%27%20OR%201%3D1--", "status")]
    [InlineData("limit=", "limit")]
    [InlineData("limit", "limit")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=101", "limit")]
    [InlineData("limit=2147483648", "limit")]
    [InlineData("limit=-1", "limit")]
    [InlineData("limit=+1", "limit")]
    [InlineData("limit=%2B1", "limit")]
    [InlineData("limit=01", "limit")]
    [InlineData("limit=0001", "limit")]
    [InlineData("limit=1.0", "limit")]
    [InlineData("limit=1e1", "limit")]
    [InlineData("limit=1%20", "limit")]
    [InlineData("limit=%091", "limit")]
    [InlineData("limit=%D9%A1", "limit")]
    [InlineData("limit=%EF%BC%91", "limit")]
    [InlineData("limit=1%00", "limit")]
    [InlineData("after=", "after")]
    [InlineData("after", "after")]
    [InlineData("after=00000000-0000-0000-0000-000000000000", "after")]
    [InlineData("after=11111111111111111111111111111111", "after")]
    [InlineData("after=SYNTHETIC_SENSITIVE_MARKER", "after")]
    [InlineData("after=%2000000001-aaaa-4bbb-8ccc-dddddddddddd", "after")]
    [InlineData("Status=New", "query")]
    [InlineData("Limit=1", "query")]
    [InlineData("After=00000001-aaaa-4bbb-8ccc-dddddddddddd", "query")]
    [InlineData("SYNTHETIC_SENSITIVE_MARKER=1", "query")]
    [InlineData("offset=0", "query")]
    [InlineData("=New", "query")]
    [InlineData("status=New&status=New", "status")]
    [InlineData("status=New&%73tatus=Closed", "status")]
    [InlineData("status=New&Status=New", null)]
    [InlineData("Status=New&status=New", null)]
    [InlineData("limit=1&limit=1", "limit")]
    [InlineData("after=00000001-aaaa-4bbb-8ccc-dddddddddddd&after=00000001-aaaa-4bbb-8ccc-dddddddddddd", "after")]
    public async Task InvalidQueryIsSafeAndValidatedBeforeOpeningStorage(string query, string? field)
    {
        var existing = Seed(1);
        var original = File.ReadAllBytes(Database.Path);
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var response = await Client.GetAsync("/cases?" + query);
            await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed", field);
            if (field is null)
            {
                // Mixed-case repeats can be identified as an unknown name or a repeated known name.
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ToArray();
                Assert.NotEmpty(fields);
                Assert.All(fields, name => Assert.True(name is "query" or "status"));
            }

            Assert.DoesNotContain(Database.Path, await response.Content.ReadAsStringAsync());
            Assert.False(File.Exists(Database.Path));
        }
        finally
        {
            File.Move(moved, Database.Path);
        }

        Assert.Equal(original, File.ReadAllBytes(Database.Path));
        Assert.Equal(existing, await ReadAsync(existing.Id));
    }
}
