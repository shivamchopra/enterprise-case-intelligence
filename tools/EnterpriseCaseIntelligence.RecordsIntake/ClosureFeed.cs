using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseCaseIntelligence.ClosureIntegration;

namespace EnterpriseCaseIntelligence.RecordsIntake;

internal static class ClosureFeed
{
    internal const int DefaultLimit = 20;
    private const int MaximumBytes = 64 * 1024;

    internal static bool TryOrigin(string value, out Uri origin)
    {
        origin = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(parsed.IdnHost, out var address) || !IPAddress.IsLoopback(address) || parsed.Port <= 0)
            return false;

        var host = parsed.HostNameType == UriHostNameType.IPv6 ? $"[{address}]" : address.ToString();
        if (value != $"http://{host}:{Decimal(parsed.Port)}") return false;
        origin = parsed;
        return true;
    }

    internal static bool TryLimit(string value, out int limit)
    {
        limit = 0;
        if (!TryPosition(value, out var number) || number is < 1 or > 100) return false;
        limit = (int)number;
        return true;
    }

    internal static async Task<ClosurePage> PullAsync(Uri origin, Checkpoint checkpoint, string? proof, int limit)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            MaxResponseDrainSize = 0
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        var query = $"?limit={Decimal(limit)}";
        if (checkpoint.StreamId is { } stream)
            query += $"&streamId={stream}&after={Decimal(checkpoint.Position)}";
        if (proof is not null) query += $"&afterProof={proof}";
        try
        {
            using var response = await client.GetAsync(new Uri(origin, "/integrations/case-closures" + query),
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Conflict) throw new IntakeFailure("pull", "source_conflict");
            if (proof is not null && response.StatusCode == HttpStatusCode.BadRequest)
                throw new IntakeFailure("pull", "checkpoint_unverified");
            if (response.StatusCode != HttpStatusCode.OK) throw new IntakeFailure("pull", "source_response");
            if (proof is not null && (!response.Headers.TryGetValues(ClosureCheckpointProof.Header, out var proofs) ||
                !proofs.SequenceEqual([proof], StringComparer.Ordinal)))
                throw new IntakeFailure("pull", "checkpoint_unverified");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentLength > MaximumBytes)
                throw InvalidPage();

            var bytes = new byte[MaximumBytes + 1];
            using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var length = await body.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token);
            if (length > MaximumBytes) throw InvalidPage();
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            try
            {
                return Parse(json.RootElement, checkpoint, limit);
            }
            catch (InvalidOperationException)
            {
                // JSON strings with invalid Unicode can fail during extraction.
                throw InvalidPage();
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new IntakeFailure("pull", "source_timeout");
        }
        catch (HttpRequestException)
        {
            throw new IntakeFailure("pull", "source_unavailable");
        }
        catch (IOException)
        {
            throw new IntakeFailure("pull", "source_unavailable");
        }
        catch (JsonException)
        {
            throw InvalidPage();
        }
    }

    private static ClosurePage Parse(JsonElement root, Checkpoint checkpoint, int limit)
    {
        RequireFields(root, "streamId", "items", "nextAfter", "hasMore");
        var streamId = ReadString(root, "streamId");
        if (!IsCanonicalId(streamId)) throw InvalidPage();
        if (checkpoint.StreamId is { } expected && expected != streamId)
            throw new IntakeFailure("pull", "source_conflict");
        var entries = root.GetProperty("items");
        var hasMore = root.GetProperty("hasMore");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > limit ||
            hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            (hasMore.GetBoolean() && entries.GetArrayLength() != limit))
            throw InvalidPage();

        var position = checkpoint.Position;
        var items = new List<ClosureNotice>(entries.GetArrayLength());
        foreach (var item in entries.EnumerateArray())
        {
            RequireFields(item, "position", "type", "caseId", "historySequence", "closedAtUtc");
            var caseId = ReadString(item, "caseId");
            var closedAt = ReadString(item, "closedAtUtc");
            var sequence = item.GetProperty("historySequence");
            if (!TryPosition(ReadString(item, "position"), out var next) || next <= position ||
                ReadString(item, "type") != "case.closed.v1" || !IsCanonicalId(caseId) || !IsTimestamp(closedAt) ||
                sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt32(out var history) || history is < 2 or > 3)
                throw InvalidPage();

            items.Add(new ClosureNotice(next, caseId, history, closedAt));
            position = next;
        }
        if (ReadString(root, "nextAfter") != Decimal(position)) throw InvalidPage();
        return new ClosurePage(streamId, items, position, hasMore.GetBoolean());
    }

    private static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidPage();
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw InvalidPage();
        if (remaining.Count != 0) throw InvalidPage();
    }

    private static string ReadString(JsonElement parent, string field)
        => parent.GetProperty(field) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw InvalidPage();

    internal static bool IsCanonicalId(string? value)
        => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");

    internal static bool IsTimestamp(string value)
        => DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    private static bool TryPosition(string value, out long position)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out position) && value == Decimal(position);

    internal static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static IntakeFailure InvalidPage() => new("pull", "invalid_page");
}

internal sealed record ClosureNotice(long Position, string CaseId, int HistorySequence, string ClosedAtUtc);
internal sealed record ClosurePage(string StreamId, IReadOnlyList<ClosureNotice> Items, long NextAfter, bool HasMore);
internal sealed record Checkpoint(string? StreamId, long Position);
