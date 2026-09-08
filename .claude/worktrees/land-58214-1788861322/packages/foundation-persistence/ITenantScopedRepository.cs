using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Foundation.Persistence;

/// <summary>
/// Marker interface for repositories whose entities implement
/// <see cref="IMustHaveTenant"/>. Repository implementations enforce
/// per-tenant filtering at the persistence boundary: cross-tenant reads
/// return <c>null</c> / empty (no diagnostic leak per ADR 0092 §"Diagnostic
/// non-leak invariant"); cross-tenant writes throw <see cref="System.ArgumentException"/>
/// (programmer bug — caller passed a mismatched tenant).
/// </summary>
/// <remarks>
/// <para>
/// <b>Introduced by ADR 0092 Step 1 (Revision 2).</b> Every block-cluster
/// repository interface in the substrate for entities bearing
/// <see cref="IMustHaveTenant"/> MUST implement this marker. The marker
/// carries ZERO contractual weight at the type-system level (no members);
/// enforcement of the per-tenant-filter invariant is the Step 4 analyzer
/// suite's responsibility, with reviewer discipline as the interim guard.
/// </para>
///
/// <para>
/// <b>Canonical method-signature shape</b> (per ADR 0092 §"Canonical
/// method-signature shape"):
/// <list type="bullet">
///   <item><c>TenantId</c> is the FIRST positional parameter on every method (analyzer-enforced at Step 4c).</item>
///   <item>Read methods (<c>GetAsync</c>, <c>List*Async</c>) filter by <c>tenantId</c> and return <c>null</c> / empty on cross-tenant — same code path as not-found (no diagnostic leak by construction; ADR 0092 §"Diagnostic non-leak invariant").</item>
///   <item>Write methods (<c>AddAsync</c>, <c>UpsertAsync</c>, etc.) assert <c>entity.TenantId == tenantId</c> at the boundary; throw <see cref="System.ArgumentException"/> on mismatch (caller bug; defensive-depth at the substrate level).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Audit emission</b> (per ADR 0092 §"Audit emission at tenant-boundary
/// violations" — amendment A6): when <c>Get*</c> finds an entity with
/// <c>entity.TenantId != tenantId</c>, the repository SHALL emit
/// <c>AuditEventType.TenantBoundaryViolation</c> (already added to
/// <c>kernel-audit</c>) before returning null. Audit payload: requested
/// tenantId, entity's actual TenantId, entity type name, request
/// correlation id — NO entity-specific content (no amounts, no
/// terminal-state strings, no display names) to avoid leaking via the
/// audit channel.
/// </para>
///
/// <para>
/// <b>Implementation pattern (per .NET-architect concern).</b> The
/// contract is explicit (per-method <c>tenantId</c> parameter), but the
/// canonical EF Core implementation captures <c>_capturedTenantId</c>
/// once at <see cref="Microsoft.EntityFrameworkCore.DbContext"/>
/// construction and applies the filter via <c>HasQueryFilter</c>. This
/// is structurally equivalent to "constructor-injection of
/// <c>ITenantContext</c>" at the implementation layer, with zero
/// per-method ergonomic cost. The contract layer pays the verbosity
/// cost (per-method <c>tenantId</c>); the implementation layer avoids
/// the recapitulation cost.
/// </para>
///
/// <para>
/// <b>Future analyzers (forward-watch markers):</b>
/// <list type="bullet">
///   <item>TODO (Step 4a — ADR 0092 Step 4a): ship <c>TenantFilterBypassAnalyzer</c> (filter-application proof).</item>
///   <item>TODO (Step 4b — ADR 0092 Step 4b): ship <c>WithoutQueryFiltersDocumentationAnalyzer</c> (opt-out documentation).</item>
///   <item>TODO (Step 4c — ADR 0092 Step 4c): ship <c>TenantIdFirstParameterAnalyzer</c> (first-parameter shape).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Note</b> (per ADR 0092 amendment B1F): the substrate has NO shared
/// <c>IRepository&lt;TEntity, TKey&gt;</c> base contract. Consumers add
/// this marker to their existing bespoke repository interfaces (opt-in
/// adoption); cohort-2 PR 0a-d migrates the financial-cluster
/// repositories (<c>IInvoiceRepository</c>, <c>IBillRepository</c>,
/// <c>IPaymentRepository</c>, <c>IJournalRepository</c>) per the standing
/// hand-off.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type the repository manages. Must implement <see cref="IMustHaveTenant"/>.</typeparam>
/// <typeparam name="TKey">The entity's primary-key type (e.g., a strongly-typed <c>InvoiceId</c> / <c>BillId</c> / <c>PaymentId</c> record-struct).</typeparam>
public interface ITenantScopedRepository<TEntity, TKey>
    where TEntity : IMustHaveTenant
{
    // No additional members on the marker; the contract is the shape of
    // the derived interface (every method takes TenantId as the first
    // parameter, analyzer-enforced at Step 4c).
}
