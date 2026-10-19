using System.Net.Http.Headers;
using System.Text.Json;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Cases;

internal static class CaseEndpoints
{
    public static void MapCaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/cases", CreateCase);
        endpoints.MapGet("/cases/{id}", GetCase);
        endpoints.MapPut("/cases/{id}/status", ChangeStatus);
    }

    private static async Task<IResult> CreateCase(HttpRequest request, SqliteCaseStore store, CancellationToken cancellationToken)
    {
        var (body, error) = await ReadBodyAsync(request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (values, errors) = ReadStrings(body!.Value, "title", "description");
        if (errors.Count > 0)
        {
            return Validation(errors);
        }

        try
        {
            var snapshot = store.Create(values["title"], values["description"]);
            return Results.Created($"/cases/{snapshot.Id:D}", Representation(snapshot));
        }
        catch (ArgumentException exception) when (exception.ParamName is "title" or "description")
        {
            var maximumLength = exception.ParamName == "title" ? 120 : 2_000;
            return Validation(exception.ParamName!, $"Use 1 to {maximumLength} characters after trimming.");
        }
    }

    private static IResult GetCase(string id, SqliteCaseStore store)
    {
        if (!TryReadId(id, out var caseId))
        {
            return Validation("id", "Use a non-empty hyphenated UUID.");
        }

        var snapshot = store.Find(caseId);
        return snapshot is null ? MissingCase() : Results.Ok(Representation(snapshot));
    }

    private static async Task<IResult> ChangeStatus(string id, HttpRequest request, SqliteCaseStore store, CancellationToken cancellationToken)
    {
        if (!TryReadId(id, out var caseId))
        {
            return Validation("id", "Use a non-empty hyphenated UUID.");
        }

        var (body, error) = await ReadBodyAsync(request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (values, errors) = ReadStrings(body!.Value, "status");
        if (errors.Count > 0)
        {
            return Validation(errors);
        }

        CaseStatus? status = values["status"] switch
        {
            "New" => CaseStatus.New,
            "InReview" => CaseStatus.InReview,
            "Closed" => CaseStatus.Closed,
            _ => null
        };

        if (status is null)
        {
            return Validation("status", "Use New, InReview or Closed.");
        }

        var result = store.ChangeStatus(caseId, status.Value);
        if (result.IsConflict)
        {
            return Problem(409, "Conflict", "invalid_transition");
        }

        return result.Case is null ? MissingCase() : Results.Ok(Representation(result.Case));
    }

    private static bool TryReadId(string value, out Guid id)
    {
        id = Guid.Empty;
        return value.Length == 36 && Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;
    }

    private static async Task<(JsonElement? Body, IResult? Error)> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(contentType.CharSet) &&
                !string.Equals(contentType.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, Problem(415, "Unsupported Media Type", "unsupported_media_type"));
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (document.RootElement.Clone(), null)
                : (null, Validation("body", "A JSON object is required."));
        }
        catch (JsonException)
        {
            return (null, Validation("body", "A valid JSON object is required."));
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 400)
        {
            return (null, Validation("body", "A valid JSON object is required."));
        }
    }

    private static (Dictionary<string, string> Values, Dictionary<string, string[]> Errors) ReadStrings(JsonElement body, params string[] fields)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var field in fields)
            {
                if (!body.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                {
                    errors[field] = ["A string value is required."];
                }
                else
                {
                    values[field] = value.GetString()!;
                }
            }

            foreach (var property in body.EnumerateObject())
            {
                if (!fields.Contains(property.Name, StringComparer.Ordinal))
                {
                    errors["body"] = ["The request contains an unsupported field."];
                }
                else if (!seen.Add(property.Name))
                {
                    errors[property.Name] = ["Supply each field once."];
                }
            }
        }
        catch (InvalidOperationException)
        {
            // JsonDocument defers decoding property names and string values until access.
            errors.Clear();
            errors["body"] = ["The request contains invalid JSON text."];
        }

        return (values, errors);
    }

    private static object Representation(CaseSnapshot snapshot) => new
    {
        id = snapshot.Id,
        title = snapshot.Title,
        description = snapshot.Description,
        status = snapshot.Status.ToString()
    };

    private static IResult MissingCase() => Problem(404, "Not Found", "case_not_found");

    private static IResult Validation(string field, string message) => Validation(new() { [field] = [message] });

    private static IResult Validation(Dictionary<string, string[]> errors) =>
        Problem(400, "Bad Request", "validation_failed", errors);

    private static IResult Problem(int status, string title, string code, Dictionary<string, string[]>? errors = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(statusCode: status, title: title, type: "about:blank", extensions: extensions);
    }
}
