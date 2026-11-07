using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCaseIntelligence.RecordsIntake;

internal static class IntakeCommand
{
    private const string Usage =
        "Use init-receipts or inspect-receipts with --receipt-file ABSOLUTE_PATH. " +
        "Use pull with --receipt-file ABSOLUTE_PATH --origin http://127.0.0.1:PORT [--limit 1-100].";

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var stage = "arguments";
        try
        {
            var command = Parse(args);
            var store = new ReceiptStore(command.Path);
            switch (command.Mode)
            {
                case "init-receipts":
                    stage = "init";
                    store.Initialize();
                    await WriteAsync(new { initialized = true });
                    break;
                case "inspect-receipts":
                    stage = "inspect";
                    var report = store.Inspect();
                    await WriteAsync(new
                    {
                        report.Checkpoint.StreamId,
                        after = ClosureFeed.Decimal(report.Checkpoint.Position),
                        receipts = report.Count
                    });
                    break;
                case "pull":
                    stage = "checkpoint";
                    var expected = store.ReadCheckpoint();
                    var page = await ClosureFeed.PullAsync(command.Origin!, expected, command.Limit);
                    stage = "apply";
                    var result = store.Apply(expected, page);
                    await WriteAsync(new
                    {
                        received = page.Items.Count,
                        applied = result.Count,
                        result.Checkpoint.StreamId,
                        after = ClosureFeed.Decimal(result.Checkpoint.Position),
                        page.HasMore
                    });
                    break;
            }
            return 0;
        }
        catch (IntakeFailure failure)
        {
            return await FailAsync(failure.Stage, failure.Category);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            SqliteException or DbUpdateException or ArgumentException or NotSupportedException)
        {
            return await FailAsync(stage, stage switch
            {
                "arguments" => "invalid_arguments",
                "output" => "output_failed",
                _ => "receipt_store"
            });
        }

        async Task WriteAsync(object value)
        {
            stage = "output";
            await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));
        }

        async Task<int> FailAsync(string failedStage, string category)
        {
            var action = failedStage switch
            {
                "arguments" => Usage,
                "init" => "Inspect the file if it exists. Initialize only a new path.",
                "inspect" => "Check the receipt path and permissions before retrying inspection.",
                _ => "Inspect the receipt file before retrying. Verify the same source is running."
            };
            await error.WriteLineAsync(JsonSerializer.Serialize(new { stage = failedStage, category, action }, JsonSerializerOptions.Web));
            return failedStage == "arguments" ? 2 : 1;
        }
    }

    private static Command Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("init-receipts" or "inspect-receipts" or "pull") || args.Length % 2 != 1)
            throw InvalidArguments();
        var mode = args[0];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
            if (!options.TryAdd(args[index], args[index + 1])) throw InvalidArguments();

        if (!options.Remove("--receipt-file", out var path) || !Path.IsPathFullyQualified(path))
            throw InvalidArguments();

        Uri? origin = null;
        var limit = ClosureFeed.DefaultLimit;
        if (mode == "pull")
        {
            if (!options.Remove("--origin", out var value) || !ClosureFeed.TryOrigin(value, out origin))
                throw InvalidArguments();
            if (options.Remove("--limit", out var text) && !ClosureFeed.TryLimit(text, out limit))
                throw InvalidArguments();
        }
        if (options.Count != 0) throw InvalidArguments();
        return new Command(mode, Path.GetFullPath(path), origin, limit);
    }

    private static IntakeFailure InvalidArguments() => new("arguments", "invalid_arguments");

    private sealed record Command(string Mode, string Path, Uri? Origin, int Limit);
}

internal sealed class IntakeFailure(string stage, string category) : Exception(category)
{
    internal string Stage { get; } = stage;
    internal string Category { get; } = category;
}
