using System.Globalization;
using System.Text.Json;
using EnterpriseCaseIntelligence.RecordsIntake;
using Microsoft.AspNetCore.Http;

namespace EnterpriseCaseIntelligence.Api.Tests.Intake;

public class ReceiptFeedTests
{
    private const string Stream = "42ec2c52-3678-4f2c-9138-b67832462418";
    private const string CaseId = "57a9162f-4b3c-4516-859f-dc7b682833cd";
    private const string OtherId = "95fd24cc-92b3-4a74-a10f-1e7ece28c335";
    private const string Time = "2026-11-05T18:00:00.0000000Z";
    private const string Marker = "SYNTHETIC_PRIVATE_MARKER";

    [Theory]
    [InlineData("http://localhost:5080")]
    [InlineData("https://127.0.0.1:5080")]
    [InlineData("http://192.0.2.1:5080")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:5080/")]
    [InlineData("http://127.0.0.1:5080/custom")]
    [InlineData("http://127.0.0.1:5080?x=1")]
    [InlineData("http://127.0.0.1:5080#mark")]
    [InlineData("http://SYNTHETIC_PRIVATE_MARKER@127.0.0.1:5080")]
    [InlineData("http://2130706433:5080")]
    [InlineData("http://[::]:5080")]
    public async Task RejectsNoncanonicalOrOutOfScopeOriginBeforeStoreAccess(string origin)
    {
        using var files = new IntakeFiles();
        (await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", origin))).Failure("arguments", "invalid_arguments", 2);
        Assert.False(File.Exists(files.Path));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://127.0.0.2:80")]
    [InlineData("http://[::1]:5080")]
    public void AcceptsCanonicalNumericLoopbackWithExplicitPort(string origin)
        => Assert.True(ClosureFeed.TryOrigin(origin, out _));

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("01")]
    [InlineData("+1")]
    public async Task RejectsInvalidLimitBeforeStoreAccess(string limit)
    {
        using var files = new IntakeFiles();
        (await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", "http://127.0.0.1:5080", "--limit", limit)))
            .Failure("arguments", "invalid_arguments", 2);
        Assert.False(File.Exists(files.Path));
    }

    [Theory]
    [InlineData("unexpected-property")]
    [InlineData("duplicate-property")]
    [InlineData("missing-field")]
    [InlineData("upper-stream")]
    [InlineData("unknown-event")]
    [InlineData("invalid-case")]
    [InlineData("invalid-unicode")]
    [InlineData("invalid-sequence")]
    [InlineData("invalid-time")]
    [InlineData("duplicate-position")]
    [InlineData("descending")]
    [InlineData("oversized-position")]
    [InlineData("numeric-position")]
    [InlineData("wrong-cursor")]
    [InlineData("empty-with-more")]
    [InlineData("short-with-more")]
    [InlineData("too-many-items")]
    [InlineData("bad-json")]
    public async Task RejectsWholeMalformedPageWithoutBindingOrApplyingPrefix(string corruption)
    {
        var second = Item("2");
        var response = corruption switch
        {
            "unexpected-property" => Page("2", [Item("1"), second[..^1] + $",\"detail\":\"{Marker}\"}}"]),
            "duplicate-property" => Page("2", [Item("1"), second[..^1] + ",\"position\":\"2\"}"]),
            "missing-field" => Page("2", [Item("1"), second.Replace($",\"closedAtUtc\":\"{Time}\"", "", StringComparison.Ordinal)]),
            "upper-stream" => Page("2", [Item("1"), second], stream: Stream.ToUpperInvariant()),
            "unknown-event" => Page("2", [Item("1"), second.Replace("case.closed.v1", Marker, StringComparison.Ordinal)]),
            "invalid-case" => Page("2", [Item("1"), Item("2", CaseId.ToUpperInvariant())]),
            "invalid-unicode" => Page("2", [Item("1"), second.Replace(CaseId, "\\uD800", StringComparison.Ordinal)]),
            "invalid-sequence" => Page("2", [Item("1"), second.Replace("\"historySequence\":3", "\"historySequence\":1", StringComparison.Ordinal)]),
            "invalid-time" => Page("2", [Item("1"), second.Replace(Time, "2026-02-30T18:00:00.0000000Z", StringComparison.Ordinal)]),
            "duplicate-position" => Page("1", [Item("1"), Item("1")]),
            "descending" => Page("1", [second, Item("1")]),
            "oversized-position" => Page("9223372036854775808", [Item("1"), Item("9223372036854775808")]),
            "numeric-position" => Page("2", [Item("1"), second.Replace("\"position\":\"2\"", "\"position\":2", StringComparison.Ordinal)]),
            "wrong-cursor" => Page("1", [Item("1"), second]),
            "empty-with-more" => Page("0", [], more: true),
            "short-with-more" => Page("1", [Item("1")], more: true),
            "too-many-items" => Page("3", [Item("1"), second, Item("3")]),
            _ => $"{{\"details\":\"{Marker}\""
        };
        using var files = new IntakeFiles();
        new ReceiptStore(files.Path).Initialize();
        await using var feed = await TestClosureSource.StartAsync(context => WriteAsync(context, response));
        var outcome = await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", feed.Origin, "--limit", "2"));
        outcome.Failure("pull", "invalid_page");
        Assert.Equal(new ReceiptInspection(new Checkpoint(null, 0), 0), new ReceiptStore(files.Path).Inspect());
    }

    [Theory]
    [InlineData("declared")]
    [InlineData("chunked")]
    [InlineData("wrong-content-type")]
    [InlineData("source-error")]
    public async Task EnforcesBoundedBodyAndKeepsSourceResponsePrivate(string mode)
    {
        using var files = new IntakeFiles();
        new ReceiptStore(files.Path).Initialize();
        await using var feed = await TestClosureSource.StartAsync(async context =>
        {
            if (mode == "source-error") context.Response.StatusCode = 500;
            context.Response.ContentType = mode == "wrong-content-type" ? "text/plain" : "application/json";
            if (mode == "declared") context.Response.ContentLength = 65_537;
            if (mode == "chunked") await context.Response.StartAsync();
            var value = mode is "declared" or "chunked" ? Page("0", []).PadRight(65_537, ' ') : Marker;
            await context.Response.WriteAsync(value);
        });
        (await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", feed.Origin)))
            .Failure("pull", mode == "source-error" ? "source_response" : "invalid_page");
        Assert.Equal(new ReceiptInspection(new Checkpoint(null, 0), 0), new ReceiptStore(files.Path).Inspect());
    }

    [Fact]
    public async Task ExactBodyLimitCanBindAnEmptyPage()
    {
        using var files = new IntakeFiles();
        new ReceiptStore(files.Path).Initialize();
        await using var source = await TestClosureSource.StartAsync(context => WriteAsync(context, Page("0", []).PadRight(65_536, ' ')));
        var output = (await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", source.Origin))).Success();
        Assert.Equal(0, output.GetProperty("received").GetInt32());
        Assert.Equal(new ReceiptInspection(new Checkpoint(Stream, 0), 0), new ReceiptStore(files.Path).Inspect());
    }

    [Fact]
    public async Task BrokenConnectionDiffersFromASuccessfulEmptyPage()
    {
        using var files = new IntakeFiles();
        new ReceiptStore(files.Path).Initialize();
        await using var source = await TestClosureSource.StartAsync(context =>
        {
            context.Abort();
            return Task.CompletedTask;
        });
        (await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", source.Origin))).Failure("pull", "source_unavailable");
        Assert.Equal(new ReceiptInspection(new Checkpoint(null, 0), 0), new ReceiptStore(files.Path).Inspect());
    }

    [Fact]
    public async Task DoesNotFollowRedirectsOrUseEnvironmentProxies()
    {
        using var files = new IntakeFiles();
        new ReceiptStore(files.Path).Initialize();
        var proxyCalls = 0;
        var sourceCalls = 0;
        await using var trap = await TestClosureSource.StartAsync(context =>
        {
            Interlocked.Increment(ref proxyCalls);
            return WriteAsync(context, Page("0", []));
        });
        await using var source = await TestClosureSource.StartAsync(context =>
        {
            Interlocked.Increment(ref sourceCalls);
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = trap.Origin + "/redirect";
            return context.Response.WriteAsync(Marker);
        });
        var env = new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = trap.Origin,
            ["http_proxy"] = trap.Origin,
            ["ALL_PROXY"] = trap.Origin,
            ["all_proxy"] = trap.Origin,
            ["NO_PROXY"] = "",
            ["no_proxy"] = ""
        };
        (await IntakeFiles.RunProcessAsync(files.Arguments("pull", "--origin", source.Origin), env)).Failure("pull", "source_response");
        Assert.Equal(1, Volatile.Read(ref sourceCalls));
        Assert.Equal(0, Volatile.Read(ref proxyCalls));
        Assert.Equal(new Checkpoint(null, 0), new ReceiptStore(files.Path).ReadContinuation().Checkpoint);
    }

    [Fact]
    public async Task DeadlineCoversStreamingTheResponseBody()
    {
        using var files = new IntakeFiles();
        new ReceiptStore(files.Path).Initialize();
        await using var source = await TestClosureSource.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{");
            await context.Response.Body.FlushAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var failed = await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", source.Origin)).WaitAsync(timeout.Token);
        failed.Failure("pull", "source_timeout");
        Assert.Equal(new Checkpoint(null, 0), new ReceiptStore(files.Path).ReadContinuation().Checkpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAttemptsWithSameCheckpointCompareDurableReceipts(bool conflicting)
    {
        using var files = new IntakeFiles();
        var store = new ReceiptStore(files.Path);
        store.Initialize();
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source = await TestClosureSource.StartAsync(async context =>
        {
            var request = Interlocked.Increment(ref arrived);
            if (request == 2) both.TrySetResult();
            await both.Task.WaitAsync(context.RequestAborted);
            Assert.Equal(TestClosureSource.Path, context.Request.Path.Value);
            Assert.Equal("?limit=20", context.Request.QueryString.Value);
            await WriteAsync(context, Page("2", [Item("1", conflicting && request == 2 ? OtherId : CaseId), Item("2", OtherId)]));
        });

        var pull = files.Arguments("pull", "--origin", source.Origin);
        var results = await Task.WhenAll(IntakeFiles.InvokeAsync(pull), IntakeFiles.InvokeAsync(pull));
        Assert.Equal(2, arrived);
        var winners = results.Where(result => result.ExitCode == 0).Select(result => result.Success().GetProperty("applied").GetInt32()).Order();
        Assert.Equal(conflicting ? new[] { 2 } : [0, 2], winners);
        if (conflicting) Assert.Single(results, result => result.ExitCode != 0).Failure("apply", "checkpoint_conflict");
        Assert.Equal(new ReceiptInspection(new Checkpoint(Stream, 2), 2), store.Inspect());
    }

    [Fact]
    public async Task PageAndContinuationRetainInt64PositionsExactly()
    {
        using var files = new IntakeFiles();
        var store = new ReceiptStore(files.Path);
        store.Initialize();
        var position = long.MaxValue.ToString(CultureInfo.InvariantCulture);
        await using var source = await TestClosureSource.StartAsync(context =>
        {
            var after = context.Request.Query["after"].ToString();
            if (after == "") return WriteAsync(context, Page(position, [Item(position)]));
            Assert.Equal(position, after);
            Assert.Equal(Stream, context.Request.Query["streamId"].ToString());
            const string proof = "8013395eb0d38aa8055bf8578d92856cb37c35653d929fd4621869717340db63";
            Assert.Equal(proof, context.Request.Query["afterProof"].ToString());
            context.Response.Headers["X-ECI-Checkpoint-Proof"] = proof;
            return WriteAsync(context, Page(position, []));
        });
        var pull = files.Arguments("pull", "--origin", source.Origin);
        Assert.Equal(position, (await IntakeFiles.InvokeAsync(pull)).Success().GetProperty("after").GetString());
        Assert.Equal(0, (await IntakeFiles.InvokeAsync(pull)).Success().GetProperty("received").GetInt32());
        Assert.Equal(new ReceiptInspection(new Checkpoint(Stream, long.MaxValue), 1), store.Inspect());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("duplicate")]
    [InlineData("old_source")]
    public async Task PositiveCheckpointRequiresExplicitSourceVerification(string acknowledgement)
    {
        using var files = new IntakeFiles();
        var store = new ReceiptStore(files.Path);
        store.Initialize();
        var receipt = new ClosureNotice(1, CaseId, 3, Time);
        store.Apply(new Checkpoint(null, 0), new ClosurePage(Stream, [receipt], 1, false));
        var before = File.ReadAllBytes(files.Path);
        await using var source = await TestClosureSource.StartAsync(context =>
        {
            var proof = context.Request.Query["afterProof"].ToString();
            Assert.Equal(64, proof.Length);
            if (acknowledgement == "old_source")
            {
                context.Response.StatusCode = 400;
                return Task.CompletedTask;
            }
            if (acknowledgement == "wrong")
                context.Response.Headers["X-ECI-Checkpoint-Proof"] = new string('0', 64);
            if (acknowledgement == "duplicate")
                context.Response.Headers.Append("X-ECI-Checkpoint-Proof", new Microsoft.Extensions.Primitives.StringValues([proof, proof]));
            return WriteAsync(context, Page("2", [Item("2", OtherId)]));
        });

        var result = await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", source.Origin));
        result.Failure("pull", "checkpoint_unverified");
        Assert.Equal(before, File.ReadAllBytes(files.Path));
    }

    [Fact]
    public async Task BoundCheckpointRejectsAWellFormedForeignStreamEvenOnAnEmptyPage()
    {
        using var files = new IntakeFiles();
        var store = new ReceiptStore(files.Path);
        store.Initialize();
        store.Apply(new Checkpoint(null, 0), new ClosurePage(Stream, [], 0, false));
        await using var source = await TestClosureSource.StartAsync(context => WriteAsync(context, Page("0", [], stream: OtherId)));
        (await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", source.Origin))).Failure("pull", "source_conflict");
        Assert.Equal(new ReceiptInspection(new Checkpoint(Stream, 0), 0), store.Inspect());
    }

    private static Task WriteAsync(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(json);
    }

    private static string Page(string after, string[] items, bool more = false, string stream = Stream)
        => $"{{\"streamId\":\"{stream}\",\"items\":[{string.Join(',', items)}],\"nextAfter\":\"{after}\",\"hasMore\":{JsonSerializer.Serialize(more)}}}";

    private static string Item(string position, string id = CaseId)
        => $"{{\"position\":\"{position}\",\"type\":\"case.closed.v1\",\"caseId\":\"{id}\",\"historySequence\":3,\"closedAtUtc\":\"{Time}\"}}";
}
