using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseProcessRecoveryTests
{
    [Fact]
    public async Task AcknowledgedWritesSurviveForcedApiProcessTermination()
    {
        using var database = new TemporaryCaseDatabase(initialize: false);
        var initialized = await ApiProcess.RunAsync("init-store", "--CaseStorage:DatabasePath", database.Path);
        Assert.Equal(0, initialized.ExitCode);
        var cases = new List<(Guid Id, string Title, string Description, string Status)>();
        var histories = new Dictionary<Guid, CaseHistoryApiTests.HistoryValue[]>();
        using (var process = await ApiProcess.StartAsync(database.Path))
        using (var client = new HttpClient { BaseAddress = process.Address, Timeout = TimeSpan.FromSeconds(10) })
        {
            foreach (var status in new[] { "New", "InReview", "Closed" })
            {
                var title = "Synthetic " + status;
                const string description = "Synthetic persisted process test";
                using var created = await client.PostAsJsonAsync("/cases", new { title, description });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                var id = body.RootElement.GetProperty("id").GetGuid();
                if (status is "InReview" or "Closed")
                {
                    using var review = await client.PutAsJsonAsync($"/cases/{id:D}/status", new { status = "InReview" });
                    Assert.Equal(HttpStatusCode.OK, review.StatusCode);
                }

                if (status == "Closed")
                {
                    using var close = await client.PutAsJsonAsync($"/cases/{id:D}/status", new { status = "Closed" });
                    Assert.Equal(HttpStatusCode.OK, close.StatusCode);
                }

                cases.Add((id, title, description, status));
                histories[id] = await CaseHistoryApiTests.ReadHistoryAsync(client, id);
            }

            await process.KillAsync();
        }

        using var restarted = await ApiProcess.StartAsync(database.Path);
        using var reader = new HttpClient { BaseAddress = restarted.Address, Timeout = TimeSpan.FromSeconds(10) };
        foreach (var expected in cases)
        {
            using var response = await reader.GetAsync($"/cases/{expected.Id:D}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(expected.Id, body.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(expected.Title, body.RootElement.GetProperty("title").GetString());
            Assert.Equal(expected.Description, body.RootElement.GetProperty("description").GetString());
            Assert.Equal(expected.Status, body.RootElement.GetProperty("status").GetString());
            Assert.Equal(4, body.RootElement.EnumerateObject().Count());
            Assert.Equal(histories[expected.Id], await CaseHistoryApiTests.ReadHistoryAsync(reader, expected.Id));
            using var retry = await reader.PutAsJsonAsync($"/cases/{expected.Id:D}/status", new { status = expected.Status });
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(histories[expected.Id], await CaseHistoryApiTests.ReadHistoryAsync(reader, expected.Id));
        }
    }
}
