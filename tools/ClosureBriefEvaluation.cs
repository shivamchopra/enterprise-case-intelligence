using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EnterpriseCaseIntelligence.Tools;

// Offline evaluation of checked-in synthetic evidence and candidate answers.
internal static partial class ClosureBriefEvaluation
{
    private const int MaximumBytes = 512 * 1024;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const string ClosureBrief = "closure_brief";
    private const string Unsupported = "unsupported_request";

    public static int Main(string[] args)
    {
        if (args is not ["--baseline", _] and not ["--score", _, _] and not ["--route-score", _, _])
        {
            Console.Error.WriteLine("Use --baseline FIXTURES, --score FIXTURES CANDIDATES, or --route-score FIXTURES PREDICTIONS.");
            return 2;
        }

        try
        {
            var fixtures = Read(args[1], EvaluationJsonContext.Default.Fixtures);
            ValidateFixtures(fixtures);
            if (args[0] == "--route-score")
                return ScoreRoutes(fixtures, Read(args[2], EvaluationJsonContext.Default.Predictions));

            var baseline = args[0] == "--baseline";
            var candidates = baseline
                ? fixtures.Select(fixture => new Candidate(fixture.Id, Draft(fixture.Request, fixture.Evidence))).ToArray()
                : Read(args[2], EvaluationJsonContext.Default.Candidates);

            var score = Score(fixtures, candidates);
            var controlsPassed = baseline ? CheckScoring(fixtures, candidates) : (int?)null;
            var report = new Report(baseline ? "rules_baseline" : "candidate", "synthetic_offline",
                fixtures.Length, score.DraftsCorrect, score.AbstentionsCorrect, controlsPassed, score.Failures);
            Console.WriteLine(JsonSerializer.Serialize(report, EvaluationJsonContext.Default.Report));
            return score.Failures.Count == 0 && (!baseline || controlsPassed == ControlCount) ? 0 : 1;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine("Unable to read the evaluation input. Use synthetic JSON files that match the documented contract.");
            return 2;
        }
    }

    private static T Read<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        using var file = File.OpenRead(path);
        var bytes = new byte[MaximumBytes + 1];
        var size = file.ReadAtLeast(bytes, bytes.Length, false);
        if (size > MaximumBytes) throw new InvalidDataException();
        return JsonSerializer.Deserialize(bytes.AsSpan(0, size), type) ?? throw new InvalidDataException();
    }

    private static void ValidateFixtures(Fixture[] fixtures)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in fixtures)
        {
            if (fixture is null || fixture.Id.Length is < 1 or > 64 ||
                fixture.Id.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-') ||
                !ids.Add(fixture.Id) || !IsCaseId(fixture.Evidence.SelectedCaseId) ||
                fixture.Expected.Facts.Any(fact => fact is null) ||
                fixture.Expected is not ({ Outcome: "draft", Reason: null, Facts.Length: 3 }
                    or { Outcome: "abstain", Reason.Length: > 0, Facts.Length: 0 }))
                throw new InvalidDataException();
        }

        if (!fixtures.Any(fixture => fixture.Expected.Outcome == "draft") ||
            !fixtures.Any(fixture => fixture.Expected.Outcome == "abstain"))
            throw new InvalidDataException();
    }

    private static Answer Draft(string request, Evidence evidence)
    {
        if (request != ClosureBrief) return Abstain(Unsupported);
        if (evidence.CaseRead is not { } detail || evidence.HistoryRead is not { } history)
            return Abstain("evidence_unavailable");
        if (detail.Id != evidence.SelectedCaseId || history.CaseId != evidence.SelectedCaseId)
            return Abstain("case_mismatch");
        if (!ValidHistory(detail.Status, history.Items)) return Abstain("inconsistent_history");
        if (detail.Status != "Closed") return Abstain("case_not_closed");

        var last = history.Items[^1];
        if (last.Kind != "StatusChanged") return Abstain("closure_transition_unrecorded");
        var id = evidence.SelectedCaseId;
        var change = $"history:{id}#{last.Sequence.ToString(CultureInfo.InvariantCulture)}";
        return new("draft", null,
        [
            new("currentStatus", detail.Status, $"case:{id}#status"),
            new("previousStatus", last.FromStatus!, change),
            new("closedAtUtc", last.RecordedAtUtc, change)
        ]);
    }

    private static bool ValidHistory(string status, HistoryEntry[] entries)
    {
        if (entries.Length is < 1 or > 3) return false;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry is null || entry.Sequence != index + 1 ||
                entry.ToStatus is not ("New" or "InReview" or "Closed") ||
                !DateTimeOffset.TryParseExact(entry.RecordedAtUtc, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _))
                return false;
            if (index == 0)
            {
                if (entry.FromStatus is not null || entry.Kind is not ("Created" or "Baseline") ||
                    (entry.Kind == "Created" && entry.ToStatus != "New")) return false;
            }
            else if (entry.Kind != "StatusChanged" || entry.FromStatus != entries[index - 1].ToStatus ||
                (entry.FromStatus, entry.ToStatus) is not (("New", "InReview") or ("InReview", "Closed")))
                return false;
        }
        return entries[^1].ToStatus == status;
    }

    private static bool IsCaseId(string value)
        => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");

    private static Answer Abstain(string reason) => new("abstain", reason, []);

    private static Result Score(Fixture[] fixtures, Candidate[] candidates)
    {
        if (candidates.Any(candidate => candidate is null)) throw new InvalidDataException();
        var byId = candidates.ToLookup(candidate => candidate.Id, StringComparer.Ordinal);
        var knownIds = fixtures.Select(fixture => fixture.Id).ToHashSet(StringComparer.Ordinal);
        var failures = new List<Failure>();
        var drafts = 0;
        var abstentions = 0;

        if (candidates.Any(candidate => !knownIds.Contains(candidate.Id)))
            failures.Add(new(null, "unexpected_candidate"));

        foreach (var fixture in fixtures)
        {
            var entries = byId[fixture.Id].Take(2).ToArray();
            if (entries.Length != 1)
            {
                failures.Add(new(fixture.Id, entries.Length == 0 ? "missing_candidate" : "duplicate_candidate"));
                continue;
            }

            var expected = fixture.Expected;
            var answer = entries[0].Answer;
            var mismatch = Compare(expected, answer);
            if (mismatch is not null) failures.Add(new(fixture.Id, mismatch));
            else if (expected.Outcome == "draft") drafts++;
            else abstentions++;
        }
        return new(drafts, abstentions, failures);
    }

    private static int ScoreRoutes(Fixture[] fixtures, RoutePrediction[] predictions)
    {
        if (predictions.Any(prediction => prediction is null) ||
            fixtures.Any(fixture => fixture.Request is not (ClosureBrief or Unsupported)))
            throw new InvalidDataException();

        var byId = predictions.ToLookup(prediction => prediction.Id, StringComparer.Ordinal);
        var knownIds = fixtures.Select(fixture => fixture.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<Candidate>();
        var failures = new List<Failure>();
        var supportedCorrect = 0;
        var unsupportedCorrect = 0;
        if (predictions.Any(prediction => !knownIds.Contains(prediction.Id)))
            failures.Add(new(null, "unexpected_prediction"));

        foreach (var fixture in fixtures)
        {
            var entries = byId[fixture.Id].Take(2).ToArray();
            var route = entries is [{ Request: ClosureBrief or Unsupported } valid] ? valid.Request! : Unsupported;
            if (entries.Length != 1)
                failures.Add(new(fixture.Id, entries.Length == 0 ? "missing_prediction" : "duplicate_prediction"));
            else if (entries[0].Request is not (ClosureBrief or Unsupported))
                failures.Add(new(fixture.Id, "invalid_prediction"));
            else if (route != fixture.Request)
                failures.Add(new(fixture.Id, route == ClosureBrief ? "false_accept" : "false_reject"));
            else if (route == ClosureBrief) supportedCorrect++;
            else unsupportedCorrect++;

            candidates.Add(new(fixture.Id, Draft(route, fixture.Evidence)));
        }

        var answers = Score(fixtures, candidates.ToArray());
        var supportedTotal = fixtures.Count(fixture => fixture.Request == ClosureBrief);
        var report = new RoutingReport(supportedTotal, supportedCorrect, fixtures.Length - supportedTotal, unsupportedCorrect,
            answers.DraftsCorrect, answers.AbstentionsCorrect, failures, answers.Failures);
        Console.WriteLine(JsonSerializer.Serialize(report, EvaluationJsonContext.Default.RoutingReport));
        return failures.Count == 0 && answers.Failures.Count == 0 ? 0 : 1;
    }

    private static string? Compare(Answer expected, Answer answer)
    {
        if (answer.Outcome != expected.Outcome) return "wrong_outcome";
        if (answer.Reason != expected.Reason) return "wrong_reason";
        if (answer.Facts.Length != expected.Facts.Length || answer.Facts.Any(fact => fact is null))
            return "wrong_facts";
        var ordered = answer.Facts.OrderBy(fact => fact.Name, StringComparer.Ordinal).ToArray();
        var reference = expected.Facts.OrderBy(fact => fact.Name, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < reference.Length; index++)
        {
            if (ordered[index].Name != reference[index].Name || ordered[index].Value != reference[index].Value)
                return "wrong_facts";
            if (ordered[index].Citation != reference[index].Citation) return "wrong_citation";
        }
        return null;
    }

    private const int ControlCount = 9;

    private static int CheckScoring(Fixture[] fixtures, Candidate[] candidates)
    {
        var draft = candidates.FirstOrDefault(candidate => candidate.Answer.Outcome == "draft");
        var abstention = candidates.FirstOrDefault(candidate => candidate.Answer.Outcome == "abstain");
        if (draft is null || abstention is null || draft.Answer.Facts.Length == 0) return 0;
        var original = draft.Answer.Facts[0];
        var rest = draft.Answer.Facts[1..];
        var wrongValue = draft.Answer with { Facts = [original with { Value = "InReview" }, .. rest] };
        var wrongCase = original.Citation.Contains("22222222-2222-4222-8222-222222222222", StringComparison.Ordinal)
            ? "11111111-1111-4111-8111-111111111111"
            : "22222222-2222-4222-8222-222222222222";
        var wrongCitation = draft.Answer with { Facts = [original with { Citation = $"case:{wrongCase}#status" }, .. rest] };
        var fabricated = draft.Answer with { Facts = [.. draft.Answer.Facts, new("closureReason", "Approved", original.Citation)] };
        var controls = new (Candidate[] Candidates, string Failure)[]
        {
            (Replace(draft, wrongValue), "wrong_facts"),
            (Replace(draft, wrongCitation), "wrong_citation"),
            (Replace(draft, fabricated), "wrong_facts"),
            (Replace(abstention, abstention.Answer with { Facts = draft.Answer.Facts }), "wrong_facts"),
            (Replace(abstention, abstention.Answer with { Reason = "unsupported_claim" }), "wrong_reason"),
            (Replace(draft, Abstain("evidence_unavailable")), "wrong_outcome"),
            (candidates.Where(candidate => candidate.Id != draft.Id).ToArray(), "missing_candidate"),
            ([.. candidates, draft], "duplicate_candidate"),
            ([.. candidates, draft with { Id = "unknown/fixture" }], "unexpected_candidate")
        };
        return controls.Count(control => Score(fixtures, control.Candidates).Failures is [{ Code: var code }] && code == control.Failure);

        Candidate[] Replace(Candidate target, Answer answer) => candidates
            .Select(candidate => candidate.Id == target.Id ? candidate with { Answer = answer } : candidate).ToArray();
    }

    private sealed record Fixture(string Id, string Request, Evidence Evidence, Answer Expected);
    private sealed record Evidence(string SelectedCaseId, CaseRead? CaseRead, HistoryRead? HistoryRead);
    private sealed record CaseRead(string Id, string Status, string? Description);
    private sealed record HistoryRead(string CaseId, HistoryEntry[] Items);
    private sealed record HistoryEntry(int Sequence, string Kind, string? FromStatus, string ToStatus, string RecordedAtUtc);
    private sealed record Answer(string Outcome, string? Reason, Fact[] Facts);
    private sealed record Fact(string Name, string Value, string Citation);
    private sealed record Candidate(string Id, Answer Answer);
    private sealed record RoutePrediction(string Id, string? Request);
    private sealed record RoutingReport(int SupportedTotal, int SupportedCorrect, int UnsupportedTotal, int UnsupportedCorrect,
        int DraftsCorrect, int AbstentionsCorrect, List<Failure> RouteFailures, List<Failure> AnswerFailures);
    private sealed record Failure(string? Id, string Code);
    private sealed record Result(int DraftsCorrect, int AbstentionsCorrect, List<Failure> Failures);
    private sealed record Report(string Mode, string Scope, int Fixtures, int DraftsCorrect, int AbstentionsCorrect,
        int? ControlsPassed, List<Failure> Failures);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 16)]
    [JsonSerializable(typeof(Fixture[]), TypeInfoPropertyName = "Fixtures")]
    [JsonSerializable(typeof(Candidate[]), TypeInfoPropertyName = "Candidates")]
    [JsonSerializable(typeof(Report))]
    [JsonSerializable(typeof(RoutePrediction[]), TypeInfoPropertyName = "Predictions")]
    [JsonSerializable(typeof(RoutingReport))]
    private sealed partial class EvaluationJsonContext : JsonSerializerContext;
}
