using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

public sealed class KernelAuditMetadataRouteTests
{
    private static readonly TenantId Tenant = new("43300000-0000-4000-8000-000000000000");
    private static readonly DateTimeOffset At = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Metadata_pages_actual_kernel_ids_in_append_order_without_payload_values()
    {
        var trail = new InMemoryAuditTrail();
        var first = await AppendAsync(trail, Tenant);
        var second = await AppendAsync(trail, Tenant);
        _ = await AppendAsync(trail, new("43300000-0000-4000-8000-000000000099"));
        var http = Context(true);
        var page = await KernelAuditMetadataRoutes.ReadAsync(http, trail, TimeProvider.System,
            At.AddMinutes(-1), At.AddMinutes(1), null, 1, CancellationToken.None);
        var wire = Wire(page);
        Assert.Equal(first, wire.GetProperty("rows")[0].GetProperty("auditId").GetGuid());
        Assert.Equal(first, wire.GetProperty("nextCursor").GetGuid());
        Assert.DoesNotContain("sensitive form input", wire.GetRawText());
        Assert.DoesNotContain("diagnostic", wire.GetRawText());
        Assert.Equal("forminst:forms/example", wire.GetProperty("rows")[0].GetProperty("identifiers").GetProperty("entity_id").GetString());
        page = await KernelAuditMetadataRoutes.ReadAsync(http, trail, TimeProvider.System,
            At.AddMinutes(-1), At.AddMinutes(1), first, 1, CancellationToken.None);
        wire = Wire(page);
        Assert.Equal(second, Assert.Single(wire.GetProperty("rows").EnumerateArray()).GetProperty("auditId").GetGuid());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Denied_metadata_read_never_queries_kernel_trail()
    {
        var trail = new CountingTrail();
        var result = await KernelAuditMetadataRoutes.ReadAsync(Context(false), trail, TimeProvider.System,
            At.AddMinutes(-1), At.AddMinutes(1), null, 100, CancellationToken.None);
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, trail.Reads);
    }

    [Fact]
    public async Task Invalid_range_and_unknown_cursor_are_explicit_refusals()
    {
        var trail = new InMemoryAuditTrail();
        var result = await KernelAuditMetadataRoutes.ReadAsync(Context(true), trail, TimeProvider.System,
            At, At.AddDays(2), null, 100, CancellationToken.None);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
        result = await KernelAuditMetadataRoutes.ReadAsync(Context(true), trail, TimeProvider.System,
            At, At.AddMinutes(1), Guid.NewGuid(), 100, CancellationToken.None);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    private static JsonElement Wire(IResult result) => JsonSerializer.SerializeToElement(
        ((IValueHttpResult)result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static DefaultHttpContext Context(bool allowed)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection()
            .AddSingleton(TestAuthorization.Gate(allowed)).BuildServiceProvider() };
        http.Features.Set(new SelectedSessionRequestPrincipal("account", Tenant,
            new PrincipalUserId("auditor"), new CanonicalPartyReference("attribution-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination"));
        http.Request.Headers.Cookie = "__Host-hl-selected=selected-handle";
        return http;
    }

    private static async Task<Guid> AppendAsync(InMemoryAuditTrail trail, TenantId tenant)
    {
        var signer = new Ed25519Signer(KeyPair.Generate());
        var payload = await signer.SignAsync(new AuditPayload(new Dictionary<string, object?>
        {
            ["entity_id"] = "forminst:forms/example", ["submission"] = "sensitive form input",
            ["diagnostic"] = "hidden reason", ["correlation_id"] = "43300000-0000-4000-8000-000000000010",
        }), At, Guid.NewGuid());
        var id = Guid.NewGuid();
        await trail.AppendAsync(new(id, tenant, new("Forms.InstanceMinted"), At, payload, []));
        return id;
    }

    private sealed class CountingTrail : IAuditTrail
    {
        public int Reads { get; private set; }
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default)
        {
            Reads++;
            throw new InvalidOperationException("Denied reads must not reach storage.");
        }
    }
}
