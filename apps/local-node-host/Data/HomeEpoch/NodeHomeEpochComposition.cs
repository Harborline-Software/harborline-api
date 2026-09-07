using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Coordination;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// Single source of truth for the node-side home-failover fencing-epoch composition (MD-2 — the joint ADR
/// 0113+0117 amendment; ADR 0135 §D3). Registers the durable store + the G-4 in-transaction fence enlister
/// over the recoverable <see cref="LocalNodeDbContext"/>, mirroring
/// <see cref="Harborline.Api.LocalNodeHost.Data.Financial.NodeRecurringInvoiceComposition"/>.
/// </summary>
/// <remarks>
/// <para>
/// The caller is responsible for having already registered
/// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>, the <see cref="IOperationVerifier"/> (the foundation
/// Ed25519 verifier — Program.cs / NodeCommsComposition registers it), and the
/// <see cref="HomeEpochEntityModule"/> as an <c>IHarborlineEntityModule</c> (Program.cs, with the other
/// host-local modules).
/// </para>
/// <para>
/// <b>The fence is a NO-OP until a doctype is activated to multi-home (MD-3/MD-4).</b> Registering the
/// adapter wires the mechanism — <c>NodeEfJournalStore</c> resolves it through the declared Platform
/// registry — but it only fires when an ambient <see cref="HomeEpochWriteScope"/> is
/// active, which no production write path opens yet. So registering this slice does NOT change any existing
/// single-device posting behaviour; it just makes the fence available for the (separately gated) multi-home
/// activation.
/// </para>
/// </remarks>
public static class NodeHomeEpochComposition
{
    /// <summary>
    /// Registers the node-resident home-epoch store + the G-4 in-transaction fence adapter.
    /// </summary>
    public static IServiceCollection AddNodeHomeEpochFence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The foundation Ed25519 verifier the store uses to re-verify roster-signed bumps. TryAdd so it
        // composes idempotently with the comms / audit registration of IOperationVerifier.
        services.TryAddSingleton<IOperationVerifier, Ed25519Verifier>();

        // The durable read/advance store (bump-on-promotion path + current-epoch reads).
        services.TryAddSingleton<IHomeEpochStore>(sp =>
            new HomeEfHomeEpochStore(
                sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>(),
                sp.GetRequiredService<IOperationVerifier>()));

        // The G-4 in-transaction fence. NodeEfJournalStore resolves the adapter through its declared
        // Platform registry; a JE post inside an ambient HomeEpochWriteScope is fenced
        // against the tenant's current home epoch in its own transaction. A no-op for every write outside
        // a scope (all single-device writes today), so existing posting tests are unaffected.
        services.TryAddSingleton<IHomeEpochFenceEnlister, HomeEpochFenceEnlister>();
        services.AddSingleton<IWriteEnlistment>(sp =>
            (IWriteEnlistment)sp.GetRequiredService<IHomeEpochFenceEnlister>());

        return services;
    }
}
