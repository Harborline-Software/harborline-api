using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Immutable-versioned, tenant-scoped store for the D4 reuse-unit primitive
/// (ADR 0135 amendment 2026-07-01). Mirrors <see cref="IFormDefinitionStore"/>: a unit id is
/// reusable across versions, each <c>(Tenant, Id, Version)</c> is a distinct immutable record,
/// and only <see cref="FormDefinitionStatus.Published"/> revisions are selected by a
/// latest-published reference — the channel through which a new unit version propagates to
/// its referencers (reuse-by-reference, not copy).
/// </summary>
/// <remarks>
/// The store is <b>kind-agnostic</b> — it stores <see cref="ReusableUnitKind.FormComponent"/>
/// and <see cref="ReusableUnitKind.WorkflowSubgraph"/> units identically. Only the form
/// resolver interprets a form-component body; a workflow unit is stored but rejected
/// fail-closed when referenced from a form until the workflow consumer lands.
/// </remarks>
public interface IReusableUnitStore
{
    /// <summary>Loads a specific immutable revision of a unit.</summary>
    /// <exception cref="ReusableUnitNotFoundException">No unit with the requested id at the
    /// requested version exists in this tenant.</exception>
    ValueTask<ReusableUnit> GetAsync(TenantId tenant, ReusableUnitId id, SemanticVersion version, CancellationToken ct = default);

    /// <summary>Loads the current published revision (highest <see cref="SemanticVersion"/>
    /// among revisions at <see cref="FormDefinitionStatus.Published"/>), or
    /// <see langword="null"/> when none is published.</summary>
    ValueTask<ReusableUnit?> GetCurrentPublishedAsync(TenantId tenant, ReusableUnitId id, CancellationToken ct = default);

    /// <summary>Registers a new unit revision. The (tenant, id, version) tuple MUST NOT
    /// already exist; revisions are immutable.</summary>
    /// <exception cref="ReusableUnitConflictException">A revision at this (tenant, id, version)
    /// is already registered.</exception>
    /// <exception cref="ReusableUnitValidationException">The unit body violates an invariant
    /// (wrong-kind body, empty body, undeclared body field, nested reference, or an over-limit
    /// body tree).</exception>
    ValueTask<ReusableUnit> RegisterAsync(ReusableUnit unit, CancellationToken ct = default);

    /// <summary>Transitions a revision from <see cref="FormDefinitionStatus.Draft"/> to
    /// <see cref="FormDefinitionStatus.Published"/>. No-op if already Published.</summary>
    ValueTask<ReusableUnit> PublishAsync(TenantId tenant, ReusableUnitId id, SemanticVersion version, CancellationToken ct = default);

    /// <summary>Transitions a Published revision to <see cref="FormDefinitionStatus.Deprecated"/>.</summary>
    ValueTask<ReusableUnit> DeprecateAsync(TenantId tenant, ReusableUnitId id, SemanticVersion version, CancellationToken ct = default);

    /// <summary>Transitions a revision to <see cref="FormDefinitionStatus.Withdrawn"/>.</summary>
    ValueTask<ReusableUnit> WithdrawAsync(TenantId tenant, ReusableUnitId id, SemanticVersion version, CancellationToken ct = default);

    /// <summary>Enumerates all units registered for a tenant, ordered by (id asc, version asc),
    /// across all lifecycle statuses.</summary>
    IAsyncEnumerable<ReusableUnit> ListByTenantAsync(TenantId tenant, CancellationToken ct = default);
}
