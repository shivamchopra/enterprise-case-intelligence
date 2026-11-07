using EnterpriseCaseIntelligence.RecordsIntake;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.Api.Tests.Intake;

public class ReceiptStoreTests
{
    private const string Stream = "42ec2c52-3678-4f2c-9138-b67832462418";
    private const string OtherStream = "9be1bc35-c615-4c07-9169-6484e5bd75ec";
    private static readonly Checkpoint Initial = new(null, 0);

    [Fact]
    public void AdvancedCheckpointAcceptsExactRedeliveryAndRejectsEveryOtherStalePage()
    {
        using var files = new IntakeFiles();
        var store = new ReceiptStore(files.Path);
        store.Initialize();
        var first = Page(1);
        var second = Page(2);
        Assert.Equal(new ApplyResult(1, new Checkpoint(Stream, 1)), store.Apply(Initial, first));
        Assert.Equal(new ApplyResult(1, new Checkpoint(Stream, 2)), store.Apply(new Checkpoint(Stream, 1), second));
        Assert.Equal(new ApplyResult(0, new Checkpoint(Stream, 2)), store.Apply(Initial, first));

        foreach (var page in new[]
        {
            new ClosurePage(Stream, [], 0, false),
            first with { StreamId = OtherStream },
            first with { Items = [first.Items[0] with { HistorySequence = 2 }] },
            first with { Items = [first.Items[0] with { ClosedAtUtc = "2026-11-05T18:00:00.0000001Z" }] },
            new ClosurePage(Stream, [first.Items[0], Page(3).Items[0]], 3, false)
        })
        {
            var error = Assert.Throws<IntakeFailure>(() => store.Apply(Initial, page));
            Assert.Equal("checkpoint_conflict", error.Category);
        }
        Assert.Equal(new ReceiptInspection(new Checkpoint(Stream, 2), 2), store.Inspect());
    }

    [Theory]
    [InlineData("BEFORE INSERT ON closure_receipts WHEN NEW.position=2", "RAISE(ABORT, 'SYNTHETIC_PRIVATE_MARKER')")]
    [InlineData("BEFORE INSERT ON closure_receipts WHEN NEW.position=1", "RAISE(IGNORE)")]
    [InlineData("BEFORE UPDATE ON receipt_checkpoint", "RAISE(ABORT, 'SYNTHETIC_PRIVATE_MARKER')")]
    [InlineData("BEFORE UPDATE ON receipt_checkpoint", "RAISE(IGNORE)")]
    public async Task ProviderWriteFailureNeverPersistsAReceiptPrefixOrTheCheckpoint(string stage, string raise)
    {
        using var files = new IntakeFiles();
        var store = new ReceiptStore(files.Path);
        store.Initialize();
        files.ExecuteFixtureSql($"CREATE TRIGGER fail_receipt {stage} BEGIN SELECT {raise}; END");
        await using var source = await TestClosureSource.StartAsync(context => context.Response.WriteAsJsonAsync(new
        {
            streamId = Stream,
            items = new[] { "1", "2" }.Select(position => new
            {
                position,
                type = "case.closed.v1",
                caseId = "57a9162f-4b3c-4516-859f-dc7b682833cd",
                historySequence = 3,
                closedAtUtc = "2026-11-05T18:00:00.0000000Z"
            }),
            nextAfter = "2",
            hasMore = false
        }));

        var args = files.Arguments("pull", "--origin", source.Origin);
        (await IntakeFiles.InvokeAsync(args)).Failure("apply", "receipt_store");
        Assert.Equal(new ReceiptInspection(Initial, 0), store.Inspect());
        files.ExecuteFixtureSql("DROP TRIGGER fail_receipt");
        Assert.Equal(2, (await IntakeFiles.InvokeAsync(args)).Success().GetProperty("applied").GetInt32());
        Assert.Equal(new ReceiptInspection(new Checkpoint(Stream, 2), 2), store.Inspect());
    }

    [Fact]
    public async Task FailedProviderCommitLeavesNoAppliedReceipts()
    {
        using var files = new IntakeFiles();
        var store = new ReceiptStore(files.Path);
        store.Initialize();
        using (var context = files.OpenContext())
        {
            context.Database.OpenConnection();
            using var reader = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            context.Database.UseTransaction(reader);
            Assert.Equal(0, context.Receipts.Count());
            var error = await Assert.ThrowsAsync<SqliteException>(() => Task.Run(() => store.Apply(Initial, Page(1))));
            Assert.Equal(5, error.SqliteErrorCode);
            reader.Rollback();
        }
        Assert.Equal(new ReceiptInspection(Initial, 0), store.Inspect());
        Assert.Equal(new ApplyResult(1, new Checkpoint(Stream, 1)), store.Apply(Initial, Page(1)));
    }

    [Theory]
    [InlineData("DELETE FROM receipt_checkpoint")]
    [InlineData("UPDATE receipt_checkpoint SET stream_id='SYNTHETIC_PRIVATE_MARKER'")]
    [InlineData("UPDATE receipt_checkpoint SET stream_id='42ec2c52-3678-4f2c-9138-b67832462418',position=1")]
    [InlineData("DELETE FROM __EFMigrationsHistory")]
    public async Task IncompatibleReceiptStateFailsBeforeContactingSource(string damage)
    {
        using var files = new IntakeFiles();
        new ReceiptStore(files.Path).Initialize();
        files.ExecuteFixtureSql(damage);
        var bytes = File.ReadAllBytes(files.Path);
        var calls = 0;
        await using var source = await TestClosureSource.StartAsync(context =>
        {
            Interlocked.Increment(ref calls);
            context.Response.StatusCode = 500;
            return Task.CompletedTask;
        });
        (await IntakeFiles.InvokeAsync(files.Arguments("pull", "--origin", source.Origin))).Failure("checkpoint", "receipt_store");
        (await IntakeFiles.InvokeAsync(files.Arguments("inspect-receipts"))).Failure("inspect", "receipt_store");
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(bytes, File.ReadAllBytes(files.Path));
    }

    private static ClosurePage Page(long position) => new(Stream,
        [new ClosureNotice(position, "57a9162f-4b3c-4516-859f-dc7b682833cd", 3, "2026-11-05T18:00:00.0000000Z")], position, false);
}
