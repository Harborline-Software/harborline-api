using System.Reflection;
using System.Text.Json;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search.Vector;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class TenantContractBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void ConstructorAndJsonRefuseBlankTenants(string value)
    {
        Assert.Throws<ArgumentException>(() => new TenantId(value));
        Assert.Throws<ArgumentException>(() => TenantId.FromString(value));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TenantId>(JsonSerializer.Serialize(value)));
    }

    [Fact]
    public void GrantRowConversionRefusesWhitespaceTenant()
    {
        var row = new GrantRow { GrantId = Guid.NewGuid().ToString(), TenantId = " ",
            SubjectId = "subject", RoleVocabulary = "test", RoleName = "reader",
            GrantedBy = "granter", Approver = "approver" };
        var method = typeof(NodeEfGrantStore).GetMethod("ToGrant", BindingFlags.NonPublic | BindingFlags.Static)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [row]));
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Equal("value", ((ArgumentException)error.InnerException!).ParamName);
    }

    [Fact]
    public void TombstoneRowConversionRefusesWhitespaceTenant()
    {
        var row = new SubjectTombstoneRow { TenantId = " ", Pseudonym = "subject", ErasedAtUnixMs = 0,
            ApprovingActorsJson = "[\"actor\"]", LegalBasis = "test" };
        var method = typeof(NodeEfSubjectTombstoneStore).GetMethod("ToTombstone", BindingFlags.NonPublic | BindingFlags.Static)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [row]));
        Assert.IsType<ArgumentException>(error.InnerException);
    }

    [Fact]
    public void LegalHoldReplayRefusesWhitespaceTenant()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tenant-contract-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new { Kind = "hold", HoldId = "hold",
                Tenant = " ", RefKind = "Subject", RefValue = "subject", Matter = "test",
                PlacedBy = "actor", PlacedAtUtc = DateTimeOffset.UnixEpoch }) + "\n");
            Assert.Throws<ArgumentException>(() => new FileSystemLegalHoldStore(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task KnnQueryRefusesWhitespaceTenant()
    {
        await using var harness = await VecTestHarness.CreateAsync();
        using var connection = new SqliteConnection();
        await Assert.ThrowsAsync<ArgumentException>(() => harness.KnnEngine.KnnAsync(
            connection, null!, " ", AuthorizedRecordScope.EntireTenant, [1f], 1, default));
    }

    [Fact]
    public async Task VectorArtifactRefusesWhitespaceTenant()
    {
        await using var harness = await VecTestHarness.CreateAsync();
        var artifact = await new StubKgEmbeddingProvider(64).EmbedAsync("record", " ", "subject", "text");
        var error = await Assert.ThrowsAsync<ArgumentException>(() => harness.Indexer().IndexArtifactAsync(
            artifact, SearchResidency.Cache,
            Authorization.TestAuthorization.AllowedDecision(new TenantId("valid"), "record")));
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public async Task BootstrapGrantReadRefusesWhitespaceTenant()
    {
        await using var harness = await BootstrapClaimRedemptionTests.Harness.CreateAsync();
        await using var context = harness.IdentityFactory.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync("UPDATE search_grants SET tenant_id = ' '");
        // A corrupt persisted row must be refused on rehydration even when its lookup key came from storage.
        var corruptKey = new TenantId { Value = " " };
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.IsSurfaceAvailableAsync(
            harness.InstallationId, corruptKey));
    }
}
