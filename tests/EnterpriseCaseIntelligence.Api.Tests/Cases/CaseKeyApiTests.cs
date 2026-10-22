using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseKeyApiTests : CaseApiTest
{
    private const string Key = "b6ab3cd4-87cf-4bdd-ae55-103ea9978018";
    private static readonly string Body = JsonSerializer.Serialize(new { title = ValidTitle, description = ValidDescription });

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, string json, string[]? keys,
        string headerName = "Idempotency-Key", string mediaType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/cases") { Content = new StringContent(json, Encoding.UTF8, mediaType) };
        if (keys is not null) Assert.True(request.Headers.TryAddWithoutValidation(headerName, keys));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task EquivalentRequestsReplayOriginalResultAtEveryStateWithoutMutation()
    {
        using var first = await SendAsync(Client, Body, [$"\"{Key}\""]);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await ReadCaseAsync(first);
        Assert.NotEqual(Guid.Parse(Key), original.Id);
        foreach (var status in new[] { "New", "InReview", "Closed" })
        {
            if (status != "New") await SetStatusAsync(original.Id, status);
            var history = StoredHistory(original.Id);
            var before = File.ReadAllBytes(Database.Path);
            var equivalent = "{\"description\":\"  Update the fictional example section.  \",\"title\":\"  Correct the sample \\u0067uide  \"}";
            using var replay = await SendAsync(Client, equivalent, [$" \t\"{Key.ToUpperInvariant()}\" \t"], "iDeMpOtEnCy-kEy");
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(original, await ReadCaseAsync(replay));
            Assert.Equal(first.Headers.Location, replay.Headers.Location);
            Assert.False(replay.Headers.Contains("Idempotency-Key"));
            Assert.Equal(status, (await ReadAsync(original.Id)).Status);
            Assert.Equal(history, StoredHistory(original.Id));
            Assert.Equal(before, File.ReadAllBytes(Database.Path));
        }

        Assert.Equal(1, Database.Count());
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Theory]
    [InlineData("Changed", ValidDescription)]
    [InlineData(ValidTitle, "Changed")]
    [InlineData("correct the sample guide", ValidDescription)]
    [InlineData("Correct  the sample guide", ValidDescription)]
    public async Task DifferentNormalizedFieldsReturnSafe422WithoutRebinding(string title, string description)
    {
        using var first = await SendAsync(Client, Body, [$"\"{Key}\""]);
        var original = await ReadCaseAsync(first);
        var before = File.ReadAllBytes(Database.Path);
        using var changed = await SendAsync(Client, JsonSerializer.Serialize(new { title, description }), [$"\"{Key}\""]);
        await AssertProblemAsync(changed, HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.DoesNotContain(Key, await changed.Content.ReadAsStringAsync());
        Assert.Equal(before, File.ReadAllBytes(Database.Path));
        Assert.Equal(original, await ReadAsync(original.Id));
    }

    [Theory]
    [InlineData("Quotes ' \" 😀\0tail", "Quotes ' \" 😀\0tail", true)]
    [InlineData("Nul\0one", "Nul\0two", false)]
    [InlineData("café", "cafe\u0301", false)]
    [InlineData("😀one", "😀two", false)]
    public async Task EqualityUsesWholeOrdinalStringsIncludingUnicodeAndEmbeddedNuls(string title, string retryTitle, bool equal)
    {
        using var first = await SendAsync(Client, JsonSerializer.Serialize(new { title, description = title }), [$"\"{Key}\""]);
        var original = await ReadCaseAsync(first);
        using var retry = await SendAsync(Client, JsonSerializer.Serialize(new { title = retryTitle, description = retryTitle }), [$"\"{Key}\""]);
        if (equal) Assert.Equal(original, await ReadCaseAsync(retry));
        else await AssertProblemAsync(retry, HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(1, Database.Count());
    }

    public static TheoryData<string[]> InvalidKeys => new()
    {
        new[] { "\"\"" }, new[] { Key }, new[] { "\"00000000-0000-0000-0000-000000000000\"" },
        new[] { "\"" + Key.Replace("-", "") + "\"" }, new[] { "\" " + Key + "\"" }, new[] { "\"" + Key + " \"" },
        new[] { "\"" + Key + "\";x=1" }, new[] { "\"" + Key + "\",\"" + Key + "\"" },
        new[] { "\"" + Key + "\"", "\"" + Key + "\"" }, new[] { "\\\"" + Key + "\\\"" },
        new[] { "\"{b6ab3cd4-87cf-4bdd-ae55-103ea9978018}\"" }, new[] { "\"b6ab3cd4-87cf-4bdd-ae55-103ea997801g\"" }
    };

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public async Task InvalidHeaderNeverClaimsKeyAndFailsBeforeStorage(string[] keys)
    {
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var invalid = await SendAsync(Client, Body, keys);
            await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed", "idempotencyKey");
            Assert.False(File.Exists(Database.Path));
        }
        finally { File.Move(moved, Database.Path); }
        Assert.Equal(0, Database.Count());
        using var corrected = await SendAsync(Client, Body, [$"\"{Key}\""]);
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Theory]
    [InlineData("{", "application/json", 400)]
    [InlineData("[]", "application/json", 400)]
    [InlineData("{\"title\":null,\"description\":\"valid\"}", "application/json", 400)]
    [InlineData("{\"title\":\" \",\"description\":\"valid\"}", "application/json", 400)]
    [InlineData("{\"title\":\"valid\",\"description\":\"valid\",\"extra\":0}", "application/json", 400)]
    [InlineData("{\"title\":\"a\",\"title\":\"b\",\"description\":\"valid\"}", "application/json", 400)]
    [InlineData("{}", "text/plain", 415)]
    public async Task KnownKeysDoNotBypassBodyValidationWhileStoreIsUnavailable(string json, string mediaType, int status)
    {
        using var first = await SendAsync(Client, Body, [$"\"{Key}\""]);
        var moved = Database.Path + ".offline";
        File.Move(Database.Path, moved);
        try
        {
            using var invalid = await SendAsync(Client, json, [$"\"{Key}\""], mediaType: mediaType);
            await AssertProblemAsync(invalid, (HttpStatusCode)status, status == 415 ? "unsupported_media_type" : "validation_failed");
            using var invalidKey = await SendAsync(Client, Body, [Key]);
            await AssertProblemAsync(invalidKey, HttpStatusCode.BadRequest, "validation_failed", "idempotencyKey");
            using var valid = await SendAsync(Client, Body, [$"\"{Key}\""]);
            await AssertProblemAsync(valid, HttpStatusCode.InternalServerError, "internal_error");
            Assert.False(File.Exists(Database.Path));
        }
        finally { File.Move(moved, Database.Path); }
    }

    [Fact]
    public async Task CorrectedBodyCanBindPreviouslyUnclaimedKeyAndMediaTypePrecedesKeyErrors()
    {
        foreach (var json in new[] { "{}", "{\"title\":\"" + new string('a', 121) + "\",\"description\":\"valid\"}",
                     "{\"title\":\"valid\",\"description\":\"" + new string('a', 2001) + "\"}" })
        {
            using var invalid = await SendAsync(Client, json, [$"\"{Key}\""]);
            await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");
        }
        using var media = await SendAsync(Client, "{}", [Key], mediaType: "text/plain");
        await AssertProblemAsync(media, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        Assert.Equal(0L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
        using var valid = await SendAsync(Client, Body, [$"\"{Key}\""]);
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
    }

    [Fact]
    public async Task KeylessDifferentKeysAndIndependentStoresCreateIndependentCases()
    {
        var ids = new HashSet<Guid>();
        foreach (var key in new string[]?[] { null, null, [$"\"{Key}\""], [$"\"{Guid.NewGuid():D}\""] })
        {
            using var response = await SendAsync(Client, Body, key);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = await ReadCaseAsync(response);
            Assert.True(ids.Add(created.Id));
            Assert.Single(StoredHistory(created.Id));
        }
        using var other = new TemporaryCaseDatabase();
        using var factory = other.CreateFactory();
        using var client = factory.CreateClient();
        using var independent = await SendAsync(client, Body, [$"\"{Key}\""]);
        Assert.True(ids.Add((await ReadCaseAsync(independent)).Id));
        Assert.Equal(4, Database.Count());
        Assert.Equal(2L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedRequestsSerializeOneWinnerAndReplayOrMismatch(bool different)
    {
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        Task<HttpResponseMessage> Start(string body) => Task.Run(async () =>
        {
            ready.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            return await SendAsync(Client, body, [$"\"{Key}\""]);
        });
        var first = Start(Body);
        var second = Start(different ? JsonSerializer.Serialize(new { title = "Other", description = ValidDescription }) : Body);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        release.Set();
        var responses = await Task.WhenAll(first, second);
        using var a = responses[0];
        using var b = responses[1];
        if (different)
        {
            var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            await ReadCaseAsync(winner);
            await AssertProblemAsync(Assert.Single(responses, r => r != winner), HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        }
        else
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
            Assert.Equal(await ReadCaseAsync(a), await ReadCaseAsync(b));
            Assert.Equal(a.Headers.Location, b.Headers.Location);
        }
        Assert.Equal(1, Database.Count());
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_status_history"));
        Assert.Equal(1L, Database.Scalar("SELECT count(*) FROM case_creation_keys"));
    }

    [Fact]
    public void ReplayAndMismatchSurviveHostReplacementWithoutReadingAnyClock()
    {
        var clock = new MovingClock();
        var key = Guid.Parse(Key);
        var store = new SqliteCaseStore(Database.Database, clock);
        var created = store.Create(ValidTitle, ValidDescription, key).Case!;
        store.ChangeStatus(created.Id, CaseStatus.InReview);
        var history = store.History(created.Id)!.ToArray();
        foreach (var days in new[] { 0, -10000, 10000 })
        {
            clock.Days = days;
            var replacement = new SqliteCaseStore(new CaseDatabase(Database.Path), clock);
            clock.Fail = true;
            Assert.Equal(created, replacement.Create(ValidTitle, ValidDescription, key).Case);
            Assert.True(replacement.Create("Other", ValidDescription, key).IsKeyReused);
            Assert.Equal(history, replacement.History(created.Id));
        }
    }

    private sealed class MovingClock : TimeProvider
    {
        public int Days { get; set; }
        public bool Fail { get; set; }
        public override DateTimeOffset GetUtcNow() => Fail ? throw new InvalidOperationException("Replay read the clock")
            : DateTimeOffset.UnixEpoch.AddDays(Days);
    }
}
