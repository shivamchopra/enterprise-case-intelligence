using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCaseIntelligence.Api.Tests.Cases;
using EnterpriseCaseIntelligence.RecordsIntake;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.Api.Tests.Intake;

public class ReceiptProcessTests
{
    [Fact]
    public async Task SeparateCommandsBindAndDrainTheRetainedCaseFileWithExactReceipts()
    {
        using var receipt = new IntakeFiles();
        using var cases = new TemporaryCaseDatabase();

        (await IntakeFiles.RunProcessAsync(receipt.Arguments("inspect-receipts"))).Failure("inspect", "receipt_store");
        Assert.False(File.Exists(receipt.Path));
        (await IntakeFiles.RunProcessAsync(receipt.Arguments("init-receipts"))).Success();
        var initialized = File.ReadAllBytes(receipt.Path);
        (await IntakeFiles.RunProcessAsync(receipt.Arguments("init-receipts"))).Failure("init", "receipt_store");
        Assert.Equal(initialized, File.ReadAllBytes(receipt.Path));
        AssertInspection((await IntakeFiles.RunProcessAsync(receipt.Arguments("inspect-receipts"))).Success(), null, "0", 0);

        var sourceBefore = File.ReadAllBytes(cases.Path);
        (await IntakeFiles.RunProcessAsync(["inspect-receipts", "--receipt-file", cases.Path])).Failure("inspect", "receipt_store");
        Assert.Equal(sourceBefore, File.ReadAllBytes(cases.Path));

        var expected = new List<(string CaseId, int Sequence, string ClosedAt)>();
        string stream;
        using (var producer = await ApiProcess.StartAsync(cases.Path))
        using (var client = new HttpClient { BaseAddress = producer.Address })
        {
            var origin = producer.Address.GetLeftPart(UriPartial.Authority);
            var binding = (await IntakeFiles.RunProcessAsync(receipt.Arguments("pull", "--origin", origin))).Success();
            stream = binding.GetProperty("streamId").GetString()!;
            Assert.True(ClosureFeed.IsCanonicalId(stream));
            AssertPull(binding, stream, "0", 0, 0, false);

            expected.Add(await CloseAsync(client, "Review the fictional handbook"));
            expected.Add(await CloseAsync(client, "Review the synthetic index"));
            var first = (await IntakeFiles.RunProcessAsync(receipt.Arguments("pull", "--origin", origin, "--limit", "1"))).Success();
            AssertPull(first, stream, "1", 1, 1, true);
        }

        using var reopened = await ApiProcess.StartAsync(cases.Path);
        using var reader = new HttpClient { BaseAddress = reopened.Address };
        expected.Add(await CloseAsync(reader, "Review the fictional appendix"));
        var newOrigin = reopened.Address.GetLeftPart(UriPartial.Authority);
        var next = (await IntakeFiles.RunProcessAsync(receipt.Arguments("pull", "--origin", newOrigin, "--limit", "1"))).Success();
        AssertPull(next, stream, "2", 1, 1, true);
        var last = (await IntakeFiles.RunProcessAsync(receipt.Arguments("pull", "--origin", newOrigin, "--limit", "1"))).Success();
        AssertPull(last, stream, "3", 1, 1, false);
        var end = (await IntakeFiles.RunProcessAsync(receipt.Arguments("pull", "--origin", newOrigin))).Success();
        AssertPull(end, stream, "3", 0, 0, false);
        AssertInspection((await IntakeFiles.RunProcessAsync(receipt.Arguments("inspect-receipts"))).Success(), stream, "3", 3);

        using var context = receipt.OpenContext();
        Assert.Equal(expected, context.Receipts.AsNoTracking().OrderBy(row => row.Position).ToArray()
            .Select(row => (row.CaseId, row.HistorySequence, row.ClosedAtUtc)));
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Single(context.Database.GetAppliedMigrations());
        Assert.Null(context.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_schema WHERE name='__EFMigrationsLock'").SingleOrDefault());

        using var other = new TemporaryCaseDatabase();
        using var replacement = await ApiProcess.StartAsync(other.Path);
        var rejected = await IntakeFiles.RunProcessAsync(receipt.Arguments("pull", "--origin", replacement.Address.GetLeftPart(UriPartial.Authority)));
        rejected.Failure("pull", "source_conflict");
        AssertInspection((await IntakeFiles.RunProcessAsync(receipt.Arguments("inspect-receipts"))).Success(), stream, "3", 3);
    }

    [Fact]
    public async Task QuiescentRestorePreservesReplayAndRequiresReceiptsFromTheSameRecoveryPoint()
    {
        using var source = new TemporaryCaseDatabase();
        using var liveReceipts = new IntakeFiles();
        using var snapshotSource = new TemporaryCaseDatabase(initialize: false);
        using var snapshotReceipts = new IntakeFiles();
        using var restoredSource = new TemporaryCaseDatabase(initialize: false);
        using var restoredReceipts = new IntakeFiles();
        using var newerReceiptCopy = new IntakeFiles();
        (await IntakeFiles.RunProcessAsync(liveReceipts.Arguments("init-receipts"))).Success();
        var keptKey = Guid.NewGuid();
        var laterKey = Guid.NewGuid();
        const string keptTitle = "Review the synthetic retained manual";
        const string laterTitle = "Review the synthetic later appendix";

        (string CaseId, int Sequence, string ClosedAt) kept;
        string stream;
        using (var producer = await ApiProcess.StartAsync(source.Path))
        using (var client = new HttpClient { BaseAddress = producer.Address })
        {
            kept = await CloseAsync(client, keptTitle, keptKey);
            var pull = await PullAsync(liveReceipts, producer);
            stream = pull.GetProperty("streamId").GetString()!;
            AssertPull(pull, stream, "1", 1, 1, false);
        }

        var inspected = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", source.Path);
        Assert.Equal(0, inspected.ExitCode);
        AssertInspection((await IntakeFiles.RunProcessAsync(liveReceipts.Arguments("inspect-receipts"))).Success(), stream, "1", 1);
        CopyStopped(source.Path, snapshotSource.Path);
        CopyStopped(liveReceipts.Path, snapshotReceipts.Path);
        var snapshotSourceBytes = File.ReadAllBytes(snapshotSource.Path);
        var snapshotReceiptBytes = File.ReadAllBytes(snapshotReceipts.Path);

        (string CaseId, int Sequence, string ClosedAt) later;
        using (var producer = await ApiProcess.StartAsync(source.Path))
        using (var client = new HttpClient { BaseAddress = producer.Address })
        {
            later = await CloseAsync(client, laterTitle, laterKey);
            AssertPull(await PullAsync(liveReceipts, producer), stream, "2", 1, 1, false);
        }

        var liveSourceBytes = File.ReadAllBytes(source.Path);
        var liveReceiptBytes = File.ReadAllBytes(liveReceipts.Path);
        CopyStopped(snapshotSource.Path, restoredSource.Path);
        CopyStopped(snapshotReceipts.Path, restoredReceipts.Path);
        CopyStopped(liveReceipts.Path, newerReceiptCopy.Path);
        var restoredInspection = await ApiProcess.RunAsync("inspect-store", "--CaseStorage:DatabasePath", restoredSource.Path);
        Assert.Equal(0, restoredInspection.ExitCode);
        AssertInspection((await IntakeFiles.RunProcessAsync(restoredReceipts.Arguments("inspect-receipts"))).Success(), stream, "1", 1);

        using var restored = await ApiProcess.StartAsync(restoredSource.Path);
        using var review = new HttpClient { BaseAddress = restored.Address };
        using var brief = await review.GetFromJsonAsync<JsonDocument>($"/cases/{kept.CaseId}/closure-brief");
        Assert.Equal("draft", brief!.RootElement.GetProperty("outcome").GetString());
        var time = brief.RootElement.GetProperty("facts").EnumerateArray().Single(fact => fact.GetProperty("name").GetString() == "closedAtUtc");
        Assert.Equal(kept.ClosedAt, time.GetProperty("value").GetString());
        Assert.Equal($"history:{kept.CaseId}#{kept.Sequence}", time.GetProperty("citation").GetString());

        using var replay = await CreateAsync(review, keptTitle, keptKey);
        Assert.Equal(kept.CaseId, replay.RootElement.GetProperty("id").GetString());
        Assert.Equal("New", replay.RootElement.GetProperty("status").GetString());
        using var current = await review.GetFromJsonAsync<JsonDocument>($"/cases/{kept.CaseId}");
        Assert.Equal("Closed", current!.RootElement.GetProperty("status").GetString());
        using var lost = await review.GetAsync($"/cases/{later.CaseId}");
        Assert.Equal(HttpStatusCode.NotFound, lost.StatusCode);
        Assert.Equal(1L, restoredSource.Count());

        var origin = restored.Address.GetLeftPart(UriPartial.Authority);
        var refused = await IntakeFiles.RunProcessAsync(newerReceiptCopy.Arguments("pull", "--origin", origin));
        refused.Failure("pull", "source_conflict");
        Assert.Equal(liveReceiptBytes, File.ReadAllBytes(newerReceiptCopy.Path));
        AssertPull(await PullAsync(restoredReceipts, restored), stream, "1", 0, 0, false);

        // Deliberate reconciliation in the isolated copy shows that the later key is unbound.
        // The restored feed can reuse a lost position, so a newer receipt file is unsafe even
        // if its checkpoint eventually exists again.
        var reconciled = await CloseAsync(review, laterTitle, laterKey);
        Assert.NotEqual(later.CaseId, reconciled.CaseId);
        AssertPull(await PullAsync(restoredReceipts, restored), stream, "2", 1, 1, false);
        using (var context = restoredReceipts.OpenContext())
        {
            Assert.Equal(new[] { kept.CaseId, reconciled.CaseId },
                context.Receipts.AsNoTracking().OrderBy(row => row.Position).Select(row => row.CaseId).ToArray());
        }

        AssertPull(await PullAsync(newerReceiptCopy, restored), stream, "2", 0, 0, false);
        using (var context = newerReceiptCopy.OpenContext())
        {
            Assert.Equal(later.CaseId, context.Receipts.AsNoTracking().Single(row => row.Position == 2).CaseId);
        }

        Assert.Equal(liveSourceBytes, File.ReadAllBytes(source.Path));
        Assert.Equal(liveReceiptBytes, File.ReadAllBytes(liveReceipts.Path));
        Assert.Equal(snapshotSourceBytes, File.ReadAllBytes(snapshotSource.Path));
        Assert.Equal(snapshotReceiptBytes, File.ReadAllBytes(snapshotReceipts.Path));
    }

    private static void CopyStopped(string source, string destination)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            Assert.False(File.Exists(source + suffix));
        File.Copy(source, destination, overwrite: false);
    }

    private static async Task<JsonElement> PullAsync(IntakeFiles files, ApiProcess source)
        => (await IntakeFiles.RunProcessAsync(files.Arguments("pull", "--origin",
            source.Address.GetLeftPart(UriPartial.Authority)))).Success();

    [Theory]
    [InlineData("receipt-row", false)]
    [InlineData("receipt-checkpoint", false)]
    [InlineData("receipt-committed", true)]
    public async Task KillingIntakeAroundCommitLeavesTheWholePriorOrAppliedPage(string stage, bool committed)
    {
        using var receipt = new IntakeFiles();
        (await IntakeFiles.RunProcessAsync(receipt.Arguments("init-receipts"))).Success();
        using var cases = new TemporaryCaseDatabase();
        using var producer = await ApiProcess.StartAsync(cases.Path);
        using var client = new HttpClient { BaseAddress = producer.Address };
        await CloseAsync(client, "Synthetic interrupted receipt A");
        await CloseAsync(client, "Synthetic interrupted receipt B");

        var pull = receipt.Arguments("pull", "--origin", producer.Address.GetLeftPart(UriPartial.Authority));
        var pipeName = "eci-" + Guid.NewGuid().ToString("N")[..16];
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var process = IntakeFiles.StartFixture([stage, pipeName, .. pull]);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            var connected = pipe.WaitForConnectionAsync(deadline.Token);
            var exited = process.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connected, exited) == exited && process.HasExited)
                Assert.Fail($"Intake fixture exited before its barrier. {await error}");
            await connected;
            var ready = new byte[1];
            await pipe.ReadExactlyAsync(ready, deadline.Token);
            Assert.Equal(1, ready[0]);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        Assert.Empty(await output);
        Assert.Empty(await error);

        var inspected = (await IntakeFiles.RunProcessAsync(receipt.Arguments("inspect-receipts"))).Success();
        var stream = inspected.GetProperty("streamId").GetString();
        Assert.Equal(committed, stream is not null);
        AssertInspection(inspected, stream, committed ? "2" : "0", committed ? 2 : 0);
        var retry = (await IntakeFiles.RunProcessAsync(pull)).Success();
        stream ??= retry.GetProperty("streamId").GetString()!;
        AssertPull(retry, stream, "2", committed ? 0 : 2, committed ? 0 : 2, false);
        AssertInspection((await IntakeFiles.RunProcessAsync(receipt.Arguments("inspect-receipts"))).Success(), stream, "2", 2);
    }

    private static async Task<JsonDocument> CreateAsync(HttpClient client, string title, Guid? key)
    {
        const string description = "Synthetic records intake test.";
        var json = JsonSerializer.Serialize(new { title, description });
        using var created = await CaseKeyApiTests.SendAsync(client, json, key is { } value ? [$"\"{value:D}\""] : null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync());
    }

    private static async Task<(string CaseId, int Sequence, string ClosedAt)> CloseAsync(HttpClient client, string title, Guid? key = null)
    {
        using var record = await CreateAsync(client, title, key);
        var id = record.RootElement.GetProperty("id").GetString()!;
        foreach (var status in new[] { "InReview", "Closed" })
        {
            using var response = await client.PutAsJsonAsync($"/cases/{id}/status", new { status });
            response.EnsureSuccessStatusCode();
        }

        using var history = await client.GetFromJsonAsync<JsonDocument>($"/cases/{id}/history");
        var last = history!.RootElement.GetProperty("items").EnumerateArray().Last();
        return (id, last.GetProperty("sequence").GetInt32(), last.GetProperty("recordedAtUtc").GetString()!);
    }

    private static void AssertInspection(JsonElement inspected, string? stream, string after, long count)
    {
        Assert.Equal(stream, inspected.GetProperty("streamId").GetString());
        Assert.Equal(after, inspected.GetProperty("after").GetString());
        Assert.Equal(count, inspected.GetProperty("receipts").GetInt64());
    }

    private static void AssertPull(JsonElement pull, string stream, string after, int received, int applied, bool more)
    {
        Assert.Equal(stream, pull.GetProperty("streamId").GetString());
        Assert.Equal(after, pull.GetProperty("after").GetString());
        Assert.Equal(received, pull.GetProperty("received").GetInt32());
        Assert.Equal(applied, pull.GetProperty("applied").GetInt32());
        Assert.Equal(more, pull.GetProperty("hasMore").GetBoolean());
    }
}
