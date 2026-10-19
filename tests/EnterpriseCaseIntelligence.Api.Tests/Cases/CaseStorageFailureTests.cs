using System.Net;
using System.Net.Http.Json;
using EnterpriseCaseIntelligence.Api.Cases;
using EnterpriseCaseIntelligence.Domain.Cases;

namespace EnterpriseCaseIntelligence.Api.Tests.Cases;

public class CaseStorageFailureTests : CaseApiTest
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnexpectedWriteRowCountRollsBackAndIsNotReportedAsSuccessOrConflict(bool creating)
    {
        var existing = await CreateAsync();
        var operation = creating ? "INSERT" : "UPDATE";
        Database.Execute($"""
            CREATE TRIGGER ignore_write BEFORE {operation} ON cases BEGIN
                DELETE FROM cases WHERE id='{existing.Id:D}';
                SELECT RAISE(IGNORE);
            END
            """);
        using var response = creating
            ? await Client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription })
            : await Client.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "InReview" });
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Database.Execute("DROP TRIGGER ignore_write");
        Assert.Equal(existing, await ReadAsync(existing.Id));
        Assert.Equal(1, Database.Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatabaseAbortsRollbackCreationAndTransitionIncludingTriggerSideEffects(bool creating)
    {
        var existing = await CreateAsync();
        var ghostId = Guid.NewGuid();
        var operation = creating ? "INSERT" : "UPDATE";
        Database.Execute($"""
            CREATE TRIGGER fail_write BEFORE {operation} ON cases BEGIN
                INSERT INTO cases VALUES ('{ghostId:D}', 'Ghost', 'Must roll back', 'New');
                SELECT RAISE(ABORT, 'SYNTHETIC_SENSITIVE_MARKER');
            END
            """);

        using var response = creating
            ? await Client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription })
            : await Client.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "InReview" });
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Database.Execute("DROP TRIGGER fail_write");
        Factory.Dispose();
        using var reopenedFactory = Database.CreateFactory();
        using var client = reopenedFactory.CreateClient();
        using var read = await client.GetAsync(PathFor(existing.Id));
        Assert.Equal(existing, await ReadCaseAsync(read));
        Assert.Equal(1, Database.Count());
        Assert.Null(new SqliteCaseStore(Database.Database).Find(ghostId));
    }

    [Fact]
    public async Task UnavailableStoreFailsSafelyWhileValidationAndLivenessRemainIndependent()
    {
        var existing = await CreateAsync();
        var moved = Database.Path + ".unavailable";
        File.Move(Database.Path, moved);
        try
        {
            using var get = await Client.GetAsync(PathFor(existing.Id));
            await AssertProblemAsync(get, HttpStatusCode.InternalServerError, "internal_error");
            using var put = await Client.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "InReview" });
            await AssertProblemAsync(put, HttpStatusCode.InternalServerError, "internal_error");
            using var create = await Client.PostAsJsonAsync("/cases", new { title = ValidTitle, description = ValidDescription });
            await AssertProblemAsync(create, HttpStatusCode.InternalServerError, "internal_error");
            using var invalid = await Client.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "bad" });
            await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed", "status");
            using var invalidCreate = await Client.PostAsJsonAsync("/cases", new { title = " ", description = ValidDescription });
            await AssertProblemAsync(invalidCreate, HttpStatusCode.BadRequest, "validation_failed", "title");
            using var health = await Client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.False(File.Exists(Database.Path));
        }
        finally
        {
            File.Move(moved, Database.Path);
        }

        Assert.Equal(existing, await ReadAsync(existing.Id));
    }

    [Theory]
    [InlineData("title", " corrupted ")]
    [InlineData("description", " ")]
    [InlineData("status", "new")]
    [InlineData("status", "0")]
    [InlineData("status", "SYNTHETIC_SENSITIVE_MARKER")]
    public async Task InvalidPersistedValuesFailWithoutRepairOrBusinessConflict(string column, string value)
    {
        var existing = await CreateAsync();
        Database.Execute($"PRAGMA ignore_check_constraints=ON; UPDATE cases SET {column}=$value WHERE id=$id",
            ("$value", value), ("$id", existing.Id.ToString("D")));
        using var get = await Client.GetAsync(PathFor(existing.Id));
        await AssertProblemAsync(get, HttpStatusCode.InternalServerError, "internal_error");
        using var put = await Client.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "InReview" });
        await AssertProblemAsync(put, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(value, Database.Scalar($"SELECT {column} FROM cases"));
        Assert.Equal(1, Database.Count());
    }

    [Fact]
    public async Task ProviderLockTimeoutIsInternalErrorAndDoesNotMutateState()
    {
        var existing = await CreateAsync();
        using (var blocker = Database.Database.Open())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            using var response = await Client.PutAsJsonAsync(PathFor(existing.Id) + "/status", new { status = "InReview" });
            await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
            transaction.Rollback();
        }

        Assert.Equal(CaseStatus.New, new SqliteCaseStore(Database.Database).Find(existing.Id)!.Status);
    }
}
