using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

/// <summary>
/// T-986: the host's kernel audit trail is durable in <c>local-node.db</c>. A "restart" is a second service
/// provider over the same encrypted file; nothing in memory survives it.
/// </summary>
public sealed class DurableKernelAuditTrailTests : IAsyncLifetime
{
    private static readonly byte[] Seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private SearchTestStore _store = null!;

    public async Task InitializeAsync() => _store = await SearchTestStore.CreateAsync();

    public async Task DisposeAsync() => await _store.DisposeAsync();

    [Fact(DisplayName = "T-986: a kernel audit event recorded before a restart can be read after it")]
    public async Task EventRecordedBeforeRestart_IsReadAfterIt()
    {
        var tenant = new TenantId("tenant-t986");
        var auditId = Guid.NewGuid();
        await using (var before = Host(_store))
        {
            var signer = before.GetRequiredService<IOperationSigner>();
            var at = DateTimeOffset.Parse("2026-09-29T10:00:00Z");
            var payload = await signer.SignAsync(
                new AuditPayload(new Dictionary<string, object?> { ["team_id"] = "team-1", ["to_party_id"] = "owner-2" }),
                at, Guid.NewGuid());
            await before.GetRequiredService<IAuditTrail>().AppendAsync(
                new AuditRecord(auditId, tenant, AuditEventType.OwnershipTransferred, at, payload, []));
        }

        await using var after = Host(SearchTestStore.Reopen(_store));
        var page = await after.GetRequiredService<IAuditEventReader>().ListAsync(
            tenant, new AuditEventReaderQuery(EventType: AuditEventType.OwnershipTransferred));

        var record = Assert.Single(page.Records);
        Assert.Equal(auditId, record.AuditId);
        Assert.True(new Ed25519Verifier().Verify(record.Payload));
    }

    internal static ServiceProvider Host(SearchTestStore store)
    {
        var nodeSigner = new NodePrincipalSigner(Seed);
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(nodeSigner)
            .AddSingleton<IOperationSigner>(nodeSigner.Signer)
            .AddSingleton<IDbContextFactory<NodeLocalSearchDbContext>>(store.Factory)
            .AddEnrollmentCompensatingControlAudit()
            .BuildServiceProvider();
    }
}
