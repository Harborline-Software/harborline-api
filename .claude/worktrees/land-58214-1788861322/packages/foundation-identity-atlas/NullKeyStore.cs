using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// Null-object <see cref="IKeyStore"/> that returns null for all queries.
/// Default registration until a real wallet/keystore workstream ships the production
/// implementation. Per ADR 0066 §Phase 3 stub-first pattern (W#55 P2d precedent).
/// </summary>
/// <remarks>
/// TODO: W#XX — replace with real wallet/keystore implementation reading from
/// kernel-security / kernel-runtime backing stores.
/// </remarks>
public sealed class NullKeyStore : IKeyStore
{
    /// <inheritdoc />
    public ValueTask<IdentityProfile?> GetIdentityProfileAsync(
        TenantId tenant,
        ActorId actor,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IdentityProfile?>(null);

    /// <inheritdoc />
    public ValueTask<KeyInfo?> GetCurrentKeyInfoAsync(
        TenantId tenant,
        ActorId actor,
        CancellationToken ct = default) =>
        ValueTask.FromResult<KeyInfo?>(null);
}
