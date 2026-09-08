using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Taxonomy.Models;

namespace Harborline.Api.Blocks.Maintenance.Models;

/// <summary>
/// A contractor or service provider that can be assigned work orders or
/// invited to submit quotes. Init-only record per ADR 0058 amendment A2;
/// migrated from the v0.x positional-record shape in W#18 Phase 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Specialty migration (ADR 0058 cross-package wiring):</b> the
/// pre-W#18 <c>Specialty</c> singular enum field has been replaced by
/// <see cref="Specialties"/> (list of <see cref="TaxonomyClassification"/>
/// nodes referencing <c>Sunfish.Vendor.Specialties@1.0.0</c>; seed shipped
/// W#18 Phase 6 / PR #346). Use
/// <see cref="VendorSpecialtyClassifications.FromLegacyEnum"/> for
/// mechanical migration of existing enum-based callers; the
/// <see cref="VendorSpecialty"/> enum is preserved through the migration
/// window.
/// </para>
/// <para>
/// <b>New init-only fields</b> per ADR 0058 §"Initial contract surface":
/// <see cref="OnboardingState"/> (required), <see cref="W9"/> (nullable
/// reference to <c>W9Document</c> entity; W#18 Phase 4),
/// <see cref="PaymentPreference"/> (nullable opaque string; Phase 4
/// will type-narrow to a payment-preference reference),
/// <see cref="Specialties"/> (defaulted empty list), and
/// <see cref="Contacts"/> (defaulted empty list of
/// <see cref="VendorContactId"/>).
/// </para>
/// </remarks>
public sealed record Vendor : IMustHaveTenant
{
    /// <summary>Unique identifier for this vendor.</summary>
    public required VendorId Id { get; init; }

    /// <summary>
    /// Tenant scope per <see cref="IMustHaveTenant"/>. Populated from the
    /// resolved <c>ITenantContext</c> at create-call-site inside
    /// <c>InMemoryMaintenanceService.CreateVendorAsync</c>; downstream filtering
    /// of <c>ListVendorsAsync</c> / <c>GetVendorAsync</c> uses this value.
    /// Added by the PR 0 Option D tenant-isolation retrofit (ADR 0091 §A2 + ADR 0092 §A6).
    /// </summary>
    public required TenantId TenantId { get; init; }

    /// <summary>Human-readable name shown in the UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Name of the primary contact person, if known.</summary>
    public string? ContactName { get; init; }

    /// <summary>Email address for the primary contact, if known.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>Phone number for the primary contact, if known.</summary>
    public string? ContactPhone { get; init; }

    /// <summary>
    /// Current lifecycle status of this vendor record. Per ADR 0108 the
    /// <c>IArchivable</c> archived state is PROJECTED onto this existing field — there is
    /// no separate <c>ArchivedAt</c> column (no rip-and-replace migration). A vendor is
    /// "archived" exactly when <see cref="VendorStatus.Inactive"/>; the
    /// <c>IMaintenanceService.ArchiveVendorAsync</c> write path transitions to it, and the
    /// default-list-exclusion invariant is honored at the service boundary keyed on this
    /// value. The marker interface itself is applied at the boundary adapter, not on this
    /// domain record (a status-derived entity carries no archival timestamp to expose).
    /// </summary>
    public required VendorStatus Status { get; init; }

    /// <summary>Onboarding-flow state per ADR 0058 (W#18 Phase 1).</summary>
    public required VendorOnboardingState OnboardingState { get; init; }

    /// <summary>
    /// Trades + service categories this vendor offers. References nodes
    /// in <c>Sunfish.Vendor.Specialties@1.0.0</c>. Empty list when no
    /// specialty has been recorded yet.
    /// </summary>
    public IReadOnlyList<TaxonomyClassification> Specialties { get; init; } = Array.Empty<TaxonomyClassification>();

    /// <summary>Reference to the vendor's W-9 document if captured (W#18 Phase 4).</summary>
    public W9DocumentId? W9 { get; init; }

    /// <summary>
    /// Opaque payment-preference identifier (e.g., ACH, check, paper).
    /// Phase 1 ships as a placeholder string; Phase 4 will type-narrow
    /// to a <c>VendorPaymentPreferenceId</c> reference.
    /// </summary>
    public string? PaymentPreference { get; init; }

    /// <summary>Multi-contact-per-vendor child references (W#18 Phase 2).</summary>
    public IReadOnlyList<VendorContactId> Contacts { get; init; } = Array.Empty<VendorContactId>();
}
