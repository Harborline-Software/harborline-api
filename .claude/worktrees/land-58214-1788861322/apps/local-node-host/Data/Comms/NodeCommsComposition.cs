using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// Single source of truth for the node-side comms (messaging) composition — the FIRST messaging doctype on
/// the live node-host path. Registers the <see cref="CommsCrdtProjection"/> (the append-log CRDT bridge over
/// the recoverable <c>NodeLocalCommsDbContext</c>) and ensures the install-level delta router exists so the
/// comms doctype can register on it as an ADDITIVE second route (the #1265 seam) alongside contacts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Doctype #2 is additive.</b> The container-bridge delta router (<c>AddHarborlineDeltaRouter</c>) is the
/// id-routed install-level delta plane; contacts registers as the first (default) doctype, and comms
/// registers as the second — one more <c>IDeltaRouter.Register</c> call (in
/// <see cref="CommsSyncBootstrapHostedService"/>), NOT a re-architecture. <c>AddHarborlineDeltaRouter</c> is
/// idempotent (TryAdd), so calling it here (as well as in <c>AddNodeContacts</c>) is safe — whichever runs
/// first installs the singleton router; the other defers.
/// </para>
/// <para>
/// <b>SC4-C2.</b> The comms projection's ONLY durable sink is the recoverable
/// <c>NodeLocalCommsDbContext</c> (the SQLCipher-keyed <c>local-node.db</c>), registered by
/// <c>AddLocalNodeSqlCipherStore</c>. No kernel CRDT-writer / per-team event log is reachable — the CRDT
/// document is in-memory convergence state; its durable projection is the recoverable relational store.
/// </para>
/// </remarks>
public static class NodeCommsComposition
{
    /// <summary>
    /// Registers the node-resident comms projection + the (idempotent) delta router. The caller is
    /// responsible for having already registered <c>IDbContextFactory&lt;NodeLocalCommsDbContext&gt;</c>
    /// (via <c>AddLocalNodeSqlCipherStore</c>) and the CRDT engine.
    /// </summary>
    public static IServiceCollection AddNodeComms(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The CRDT engine (YDotNet/Yjs, MIT) — idempotent with AddNodeContacts' call.
        services.AddHarborlineCrdtEngine();
        // SECURITY (#1277 B1a) — the merge-path signature verifier the projection enforces on every inbound
        // message before it reaches the durable EF read store. Stateless Ed25519 verifier, singleton +
        // TryAdd so it composes idempotently with any other registration of IOperationVerifier.
        services.TryAddSingleton<IOperationVerifier, Ed25519Verifier>();

        // ── Enrollment Phase B — PRODUCTION FORGE-PROOF WIRING (#1277-B1b, closes B1 end-to-end). ──────────
        //
        // The #1288 M1 carry-forward: Phase A built the forge-proof rosterBinding overload + bite-proved it on
        // the merge path, but the SHIPPING registration here passed rosterBinding: null — so production comms
        // merge ran B1a-integrity-only and #1277-B1b was NOT closed end-to-end. Phase B wires the rosterBinding
        // Func from the install-level NodeTeamRoster (seeded at bootstrap with the genesis self-admission), so
        // the LIVE merge gate now enforces forge-proof party↔key attribution: a forged-foreign-party peer
        // message is DROPPED in production, not just in a unit test. An EMPTY roster cannot occur — the genesis
        // (self) is always in its own roster, so a fresh single-user node still forge-proves its own messages
        // and is not bricked.
        //
        // The install-level id-routed delta plane — idempotent (TryAdd) with AddNodeContacts. Registered BEFORE
        // the conversation registry so the registry's IDeltaRouter dependency resolves.
        services.AddHarborlineDeltaRouter();

        // ── C1 — CONVERSATION REGISTRY (one CRDT document per conversation). ─────────────────────────────────
        //
        // The registry lazily creates + registers one CommsCrdtProjection per conversation (the team channel +,
        // later, DMs). It is built via a FACTORY that resolves NodeTeamRoster and passes its ForgeProofBinding as
        // the rosterBinding for EVERY conversation's projection — so a DM is attribution-verified exactly as a
        // team message. NodeTeamRoster is registered by the composition root (Program.cs) BEFORE this; if a host
        // did not register it (a minimal DI-composition test), GetService returns null and the projections fall
        // back to the B1a integrity gate (rosterBinding: null) — non-bricking, identical to pre-Phase-B. The
        // empty-roster case cannot occur in production: the genesis (self) is always in its own roster.
        // ── C4 — DM CONTENT ENCRYPTION key provider (X25519-ECDH + HKDF per-conversation key). ────────────────
        //
        // SECURITY — FAIL-CLOSED in production (B1, sec-eng deep-review of PR #1325). A dm: body is SEALED to a
        // per-conversation key only the two participants can derive (DerivedDmConversationKeyProvider over a
        // participant-DM-key resolver). The CONSTRUCTION is sound; the *key distribution* is NOT delivered until C5.
        //
        // C4 must NOT register a party-id-derivable resolver in production. The earlier C4 build wired
        // SeedDerivedParticipantDmKeyResolver here — a resolver that derives every party's DM PRIVATE key from a
        // compile-time constant keyed only by party id (root seed deliberately not mixed in). Anyone holding the
        // binary could then derive any party's key by claiming that party's id (the "leak guard" in the provider is
        // an IDENTITY check, not a cryptographic one) — so the dev-gate, not crypto, was load-bearing for DM
        // confidentiality. That stand-in is now TEST-ONLY (it lives in the test assembly) and is unreferenceable
        // from production code.
        //
        // AddNodeComms therefore registers the fail-closed null-object NoDmConversationKeyProvider: it derives NO
        // keys, so a MINIMAL DI graph treats every dm: body as opaque (CanSealConversation == false) and the DM
        // route fails closed (HandleDmAppendAsync returns 503 dm_seal_unavailable when it cannot seal — plaintext
        // is NEVER sent on the team-wide plane). The REAL roster-bound resolver (per-node-secret, root-seed-bound
        // DM keys distributed on the enrollment wire, NOT party-id-derivable) lands in C5 and is wired by the FULL
        // host via AddRosterBoundDmKeyProvider (Program.cs), which Replaces this NoDm default. With that resolver +
        // its real no-leak test GREEN (three sec-eng rounds) + post-C5 cross-machine enrollment verified, the DM
        // surface now SHIPS (CommsDmFeatureFlag default ON, kill-switch). Confidentiality rests on the C5 resolver,
        // not on the route flag — so it is safe to ship the surface ON (the inverse of the old load-bearing gate).
        //
        // TryAdd so the C4/C5 test composition (which DOES wire the test-double resolver) can pre-register a real
        // DerivedDmConversationKeyProvider for the construction tests; the production graph never does.
        services.TryAddSingleton<IDmConversationKeyProvider>(_ => new NoDmConversationKeyProvider());

        services.AddSingleton<CommsConversationRegistry>(sp =>
        {
            var roster = sp.GetService<NodeTeamRoster>();
            return new CommsConversationRegistry(
                sp.GetRequiredService<ICrdtEngine>(),
                sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>(),
                sp.GetRequiredService<IOperationVerifier>(),
                sp.GetRequiredService<IDeltaRouter>(),
                sp.GetRequiredService<ILoggerFactory>(),
                rosterBinding: roster is null ? null : roster.ForgeProofBinding,
                dmKeyProvider: sp.GetRequiredService<IDmConversationKeyProvider>(),
                // C5 — participant-scoped routing: resolve a connected peer's party id from its transport pubkey so a
                // dm: stream ships only to its two participants. Null in a minimal/team-only host (no routing filter;
                // the seal still protects the body). Bound to the SAME live roster so a freshly admitted peer routes.
                peerPartyResolver: roster is null
                    ? null
                    : key => roster.PartyIdForTransportKey(key));
        });

        // The team-channel projection — the well-known "team" conversation — resolved from the registry. Kept as
        // a directly-injectable singleton so HostedCommsApiEndpoint (and existing consumers) get the team channel
        // projection without each having to ask the registry by id. (DM projections are obtained via the registry
        // by conversation id; the bare CommsCrdtProjection injection is the team channel.)
        services.AddSingleton<CommsCrdtProjection>(sp =>
            sp.GetRequiredService<CommsConversationRegistry>().GetOrCreate(CommsConversation.TeamConversationId));

        // ── DM FEATURE FLAG (SHIPPED, default ON; kill-switch HARBORLINE_COMMS_DM_DISABLED). ─────────────────────
        //
        // (See AddRosterBoundDmKeyProvider below — the C5 production wiring that OVERRIDES the fail-closed NoDm
        // provider above with the real roster-bound resolver once the root secret + active team + party id are in
        // scope at bootstrap. AddNodeComms alone stays fail-closed so a minimal DI graph cannot derive a DM key.)
        //
        // The DM body is SEALED end-to-end (C4 content seal + C5 roster-bound, node-secret keys), so the DM route
        // surface (the dm/{otherPartyId} resolve route in CommsRoutes) now SHIPS — the flag defaults ON. The
        // kill-switch (HARBORLINE_COMMS_DM_DISABLED truthy) removes ONLY the DM route in an incident; the team
        // channel + the generic conversation-addressed routes are UNAFFECTED. TryAdd so a test can pre-register an
        // explicitly-stated flag. NOTE: the flag is NOT a confidentiality boundary — DM secrecy rests on the C5
        // crypto, not on this route gate (the B1 lesson from #1325: it is safe to ship the surface ON precisely
        // because the crypto, not the gate, is load-bearing for confidentiality).
        services.TryAddSingleton<CommsDmFeatureFlag>(_ => new CommsDmFeatureFlag());

        return services;
    }

    /// <summary>
    /// C5 — the PRODUCTION DM-key wiring: OVERRIDE the fail-closed <see cref="NoDmConversationKeyProvider"/> that
    /// <see cref="AddNodeComms"/> registered with the REAL roster-bound resolver. Call this from the composition
    /// root (Program.cs) once the install root secret + the active team id + the active member's party id are in
    /// scope. After this, a <c>dm:</c> projection seals/unseals bodies with a per-conversation key the two
    /// participants derive via X25519 ECDH (active member's seed-derived DM PRIVATE key × the peer's roster-published
    /// DM PUBLIC key) — the increment that delivers DM confidentiality.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a SEPARATE method that overrides, not part of <see cref="AddNodeComms"/>.</b> The fail-closed
    /// default must remain the posture of <see cref="AddNodeComms"/> alone — a minimal DI graph (no root secret / no
    /// roster / no active team) MUST NOT be able to derive a DM key (the B1 structural fence from PR #1325). Only a
    /// FULL host that supplies the node-secret root + the verified roster gets the real resolver. This method does an
    /// explicit <see cref="ServiceCollectionDescriptorExtensions.Replace">Replace</see> (not TryAdd) so it deposes
    /// the NoDm registration deterministically.
    /// </para>
    /// <para>
    /// <b>Not party-id-derivable (the C4-hole fix).</b> The resolver is <see cref="RosterDmKeyResolver"/>, whose
    /// active-member DM private key is HKDF(node-root-secret, teamId) — a function of the NODE root secret, never of
    /// the party id — so a non-participant cannot derive a participant's key by claiming its id. The arch-fence
    /// asserts the shipped assembly's IParticipantDmKeyResolver is THIS roster-bound type (node-secret), never a
    /// party-id-derivable one.
    /// </para>
    /// <para>
    /// <b>The DM private key FOLLOWS THE ACTIVE TEAM (bug-1332).</b> The resolver is wired with the
    /// <c>IActiveTeamAccessor</c> so its DM private key REFRESHES to the joined team on a wire-enrollment join —
    /// mirroring the <c>LocalNodeWorker</c> gossip daemon-rebind. <paramref name="bootTeamId"/> is the floor (the
    /// genesis team) the key is scoped to until/unless the active team switches; after a joiner adopts the admitter's
    /// team its DM private key becomes HKDF(root, joinedTeamId), matching the joined-team DM PUBLIC key it published →
    /// the cross-team DM ECDH agrees → both participants decrypt. A single-team node never enters the rebind path.
    /// </para>
    /// </remarks>
    /// <param name="services">The host service collection (after <see cref="AddNodeComms"/>).</param>
    /// <param name="rootSecret">The install's 32-byte root secret (the DM private key derives from it; copied
    /// internally — the caller may zero its buffer after).</param>
    /// <param name="bootTeamId">The boot/genesis team id (string form) the DM keypair is scoped to until the active
    /// team switches (the single-user floor).</param>
    /// <param name="activeMemberPartyId">The active member's party id (the comms author / "me" in the ECDH). Stable
    /// across a join — only the team id follows the active team.</param>
    public static IServiceCollection AddRosterBoundDmKeyProvider(
        this IServiceCollection services, byte[] rootSecret, string bootTeamId, string activeMemberPartyId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(rootSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeMemberPartyId);
        if (rootSecret.Length == 0)
        {
            throw new ArgumentException("Root secret must be non-empty.", nameof(rootSecret));
        }

        // Copy the secret so a caller that zeroes its buffer post-registration does not zero ours.
        var seed = (byte[])rootSecret.Clone();

        // Replace (deterministically deposes the NoDm TryAdd from AddNodeComms) with the roster-bound resolver +
        // the unchanged C4 derived-ECDH provider over it. Resolved via DI so the NodeTeamRoster singleton is the
        // SAME live roster the merge gate + trust policy use (so a freshly admitted peer's DM key is visible), and so
        // the IActiveTeamAccessor singleton is the SAME accessor the join orchestrator + the worker observe (so a
        // joiner's DM private key re-keys to the joined team on the SAME ActiveChanged that rebinds the daemon).
        services.Replace(ServiceDescriptor.Singleton<IDmConversationKeyProvider>(sp =>
        {
            var roster = sp.GetRequiredService<NodeTeamRoster>();
            // GetService (nullable), not GetRequiredService: the full host ALWAYS registers IActiveTeamAccessor (the
            // join orchestrator + worker resolve it), so the resolver follows the active team in production. A minimal
            // DI graph that omits it (a focused arch-fence / construction test) gets a null accessor and the resolver
            // pins to the boot team — the safe single-team floor, identical to pre-bug-1332 behaviour.
            var activeTeam = sp.GetService<IActiveTeamAccessor>();
            var resolver = new RosterDmKeyResolver(seed, bootTeamId, activeMemberPartyId, roster, activeTeam);
            return new DerivedDmConversationKeyProvider(resolver);
        }));

        return services;
    }
}
