using System;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// The node-resident <b>tenant governance state</b> record — the durable home of the instance
/// <see cref="SetupPhase"/> (ADR 0144 AD.1 "Record home: a <c>setupPhase</c> field on the
/// <c>TenantGovernanceState</c> record that ADR 0144's seed-graph genesis creates"). One row per tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives node-side today.</b> ADR 0144's seed-graph (<c>ISeedGraph.Instantiate</c>) and its
/// canonical <c>TenantGovernanceState</c> home in <c>foundation-authorization</c> are the ADR's Phase-2
/// build — <em>not yet built</em>. This record is the dogfood-grade, node-resident context holder of the one field
/// slice B5 owns (the <c>setupPhase</c>), scoped to the local-node-host governance seam. When the 0144
/// Phase-2 seed-graph lands, this node-side state reconciles into (or is superseded by) that record — the
/// field name and the two-value domain (<c>setup</c> / <c>operating</c>) are chosen to graduate cleanly.
/// The reconciliation seam is called out in the B5 council-request beacon.
/// </para>
/// <para>
/// <b>Genesis writes <see cref="SetupPhase.Setup"/>; the founder DECLARES the transition.</b> A fresh
/// instance is born in setup phase (<see cref="Genesis"/>). The move to <see cref="SetupPhase.Operating"/>
/// is <em>founder-declared</em> (<see cref="LockedDown"/>), never auto-detected on a heuristic — auto-flip
/// would be the silent state change AD.3 forbids. Locking down is the SAFE direction (reversible, reduces
/// surface) and is therefore unguarded; only re-entry (unlock) is guarded.
/// </para>
/// <para>
/// <b>Provenance stamps.</b> <see cref="EnteredSetupAt"/> records genesis; <see cref="EnteredOperatingAt"/>
/// records the founder's declared "start running your business" milestone (null while still in setup) — a
/// small honest audit of when the instance lifecycle turned, useful to the Harborline App + diagnostics.
/// </para>
/// </remarks>
public sealed record TenantGovernanceState
{
    /// <summary>The tenant this governance state is scoped to — the active-team-derived tenant
    /// (<c>ActiveTeamTenantContext.ProjectTenantId</c>; ADR 0032). One row per tenant.</summary>
    public required string TenantId { get; init; }

    /// <summary>The instance lifecycle phase. Genesis writes <see cref="SetupPhase.Setup"/>; the founder's
    /// declared transition sets <see cref="SetupPhase.Operating"/>.</summary>
    public required SetupPhase SetupPhase { get; init; }

    /// <summary>Wall-clock instant genesis established this tenant's governance state (born in setup).</summary>
    public required DateTimeOffset EnteredSetupAt { get; init; }

    /// <summary>Wall-clock instant the founder declared setup complete (setup → operating), or
    /// <see langword="null"/> while the instance is still in setup phase.</summary>
    public DateTimeOffset? EnteredOperatingAt { get; init; }

    /// <summary>The genesis state for <paramref name="tenantId"/> — born in <see cref="SetupPhase.Setup"/>
    /// at <paramref name="now"/>. The single constructor the genesis path uses so "genesis = setup" is
    /// expressed in exactly one place.</summary>
    public static TenantGovernanceState Genesis(string tenantId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        return new TenantGovernanceState
        {
            TenantId = tenantId,
            SetupPhase = SetupPhase.Setup,
            EnteredSetupAt = now,
            EnteredOperatingAt = null,
        };
    }

    /// <summary>
    /// The operating state — the founder's declared "start running your business" transition. Idempotent by
    /// construction: applied to an already-operating state it preserves the original
    /// <see cref="EnteredOperatingAt"/> (the first declaration wins; a re-declare is a no-op, never a
    /// re-stamp). Applied to a setup state it flips the phase and stamps <paramref name="now"/>.
    /// </summary>
    public TenantGovernanceState LockedDown(DateTimeOffset now) => SetupPhase == SetupPhase.Operating
        ? this
        : this with { SetupPhase = SetupPhase.Operating, EnteredOperatingAt = now };
}
