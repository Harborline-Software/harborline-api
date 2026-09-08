using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Coordination;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// Single source of truth for the node-side audit WRITE + READ composition (ADR 0126 — T4 audit
/// system-of-record flip). Registers exactly the audit-write enlister + the audit read-model over an
/// ALREADY-registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Extracted so the composition root (<c>Program.cs</c>) and the SC4-T9(b) Layer-2 runtime DI-graph
/// assertion (<c>Sc4RecoverabilityGuardTests</c>) register the EXACT same audit slice — no test/prod
/// drift in WHAT the gate verifies. The security SPOT-CHECK can read these two methods to audit every
/// registration the audit path adds. Mirrors <c>NodeFinancialPostingComposition.AddNodeFinancialPosting</c>.
/// </para>
/// <para>
/// <b>SC4-C2 conditions enforced by the shape of these registrations</b> (the SC4-T9(b) gate asserts
/// them; ADR 0126 §"SC-4 / SC4-T9(b) implications"):
/// <list type="bullet">
///   <item>(a) the ONLY persistence sink is the recoverable <c>local-node.db</c> — the
///     <see cref="NodeAuditWriteEnlister"/> stages onto the SAME <see cref="LocalNodeDbContext"/> the
///     JE write commits, and the <see cref="NodeAuditEventReader"/> reads <c>local-node.db</c>. No
///     audit row ever reaches a seed-keyed KV.</item>
///   <item>(b) NO <c>IDomainEventPublisher</c>/<c>IDomainEventStore</c> is registered here (Noop
///     posture holds).</item>
///   <item>(c) NO kernel CRDT writer (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and NO per-team
///     <c>FileBackedEventLog</c>/<c>IEventLog</c> is registered — the enlister + reader are pure
///     compute + recoverable EF, taking NO <c>IEventLog</c> dependency. This is the property that keeps
///     the Layer-1 IL scan + Layer-2 DI-graph gate green and closes the orphan vector.</item>
///   <item>(d) the reader resolves node EF over <c>local-node.db</c>, never a seed-keyed per-team KV
///     store.</item>
/// </list>
/// </para>
/// </remarks>
public static class NodeAuditComposition
{
    /// <summary>
    /// Registers the node-side audit WRITE enlister (ADR 0126 §D2 / OQ2 = atomic). Wiring this makes
    /// <see cref="NodeAuditWriteEnlister"/> resolvable, which <see cref="Financial.NodeEfJournalStore"/>
    /// resolves through the declared Platform registry — so a posted JE's audit row joins the JE's
    /// <c>local-node.db</c> transaction. The audit instant comes from the carried decision; the
    /// enlister never reads a current clock while recording the admitted act.
    /// </summary>
    public static IServiceCollection AddNodeAuditWrites(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The atomic-audit enlister. NodeEfJournalStore resolves it through the declared Platform
        // registry, so JE-posted audit rows join the JE transaction.
        //
        // The enlister accepts the exact posting AuthorizationDecision as an argument. It does not
        // resolve caller identity, grants, or authority facts from request-local or ambient state.
        // attribution. The scope reader is pure compute, not an IEventLog / CRDT / KV sink, so SC4-C2 is
        // unaffected.
        // ADR 0135 per-event signing (the pre-multi-device PASS-gate) — the enlister is built via a
        // FACTORY that OPTIONALLY pulls the node's IOperationSigner (the RootSeedHex→Ed25519 identity via
        // NodePrincipalSigner, ADR 0118 custody ladder) WHEN IT IS REGISTERED. When the node composition
        // root has wired NodePrincipalSigner (Program.cs always does), JE-posted audit rows are signed
        // (v1, Verified). When it is NOT registered (the SC4-T9(b) guard's minimal audit graph + any
        // build without foundation crypto), GetService returns null → rows stay unsigned (v0, NotSigned)
        // and the SC4 graph still resolves the enlister with no new hard dependency. This keeps the
        // signer OPTIONAL at the composition seam — no SC4-C2 violation (the signer is pure crypto, not
        // an IEventLog / CRDT / KV sink), and existing posting tests are unaffected.
        services.AddSingleton<INodeAuditWriteEnlister>(sp => new NodeAuditWriteEnlister(
            sp.GetService<NodePrincipalSigner>()?.Signer));
        services.AddSingleton<IWriteEnlistment>(sp =>
            (IWriteEnlistment)sp.GetRequiredService<INodeAuditWriteEnlister>());

        return services;
    }

    /// <summary>
    /// Registers the node-side audit READ-model (ADR 0126 §D3) over the recoverable
    /// <c>node_audit_events</c> table. The caller is responsible for having registered
    /// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> (the reader's backing) + the
    /// <see cref="AuditEventEntityModule"/> (the table's schema).
    /// </summary>
    public static IServiceCollection AddNodeAuditReads(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ADR 0135 per-event signing (the pre-multi-device PASS-gate) — the reader is built via a
        // FACTORY that OPTIONALLY supplies the signature-VERIFICATION context (the current node issuer +
        // the foundation IOperationVerifier) WHEN BOTH are registered. With the context wired (Program.cs
        // always registers NodePrincipalSigner + Ed25519Verifier), the reader performs REAL Ed25519
        // re-verification of signed rows at read time — a tampered signed row reads VerificationFailed.
        // Without it (the SC4 guard's minimal audit graph, or any build lacking the node verifier), the
        // context is null → the classifier keeps the conservative chain-only posture and the SC4 reader
        // still resolves with no new hard dependency. The verifier is stateless foundation crypto, NOT an
        // IEventLog / CRDT / KV sink — so SC4-C2 is unaffected.
        services.AddSingleton(sp =>
        {
            var signer = sp.GetService<NodePrincipalSigner>();
            var verifier = sp.GetService<IOperationVerifier>();
            NodeAuditSignatureVerificationContext? verification =
                signer is not null && verifier is not null
                    ? new NodeAuditSignatureVerificationContext(signer.Signer.IssuerId, verifier)
                    : null;
            return new NodeAuditEventReader(
                sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<LocalNodeDbContext>>(),
                verification);
        });

        return services;
    }
}
