using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// An <b>owned calendar</b> — the collection a <see cref="CalendarEvent"/> belongs to (the demand-side
/// grouping the calendar-productization design note (#149) introduces as the primary organizing unit).
/// This is the entity that fills the <see cref="CalendarId"/> extension seam <see cref="CalendarEvent"/>
/// shaped-but-never-populated: "my personal calendar", "the care team calendar", "Exam Room 3".
/// </summary>
/// <remarks>
/// <para>
/// <b>Named to contrast with <see cref="SharedCalendar"/>.</b> Design note §1.1 keeps two "calendar"
/// nouns apart: THIS type is the demand-side COLLECTION an event is owned by; the orthogonal
/// <see cref="SharedCalendar"/> / <see cref="CalendarSubscription"/> machinery is the supply-side layer
/// of availability EXCEPTIONS (holidays / closures). "Add a calendar" creates a new
/// <see cref="OwnedCalendar"/>, never a shared availability-exception calendar. (The type is
/// <c>OwnedCalendar</c>, not a bare <c>Calendar</c>, because <c>Calendar</c> is a namespace segment
/// throughout this block + the node — every calendar-domain type is prefixed, e.g. <c>CalendarEvent</c>,
/// <c>SharedCalendar</c>.)
/// </para>
/// <para>
/// <b>Tenant-scoped, composite-keyed.</b> Like every node-local master, the store keys a calendar by the
/// composite <c>(TenantId, Id)</c> so a foreign-tenant read returns null (cross-tenant isolation).
/// </para>
/// <para>
/// <b>Default calendar (design §1.4).</b> Exactly one <see cref="IsDefault"/> calendar is provisioned per
/// instance at genesis, bound to the founder principal. Its <see cref="Name"/> is the i18n KEY
/// <see cref="DefaultNameKey"/> (NOT a hardcoded English literal baked into a seed row — §1.6): the
/// Harborline App resolves the display name ("My calendar" / localized) in the viewer's locale. User-created
/// calendars carry a free-text <see cref="Name"/> stored verbatim (user data, never a translation key).
/// </para>
/// </remarks>
public sealed class OwnedCalendar
{
    /// <summary>
    /// The i18n key the provisioned default calendar's <see cref="Name"/> carries (design §1.6). The
    /// Harborline App resolves this under the <c>calendar:</c> locale block in the viewer's locale ("My
    /// calendar"); it is deliberately NOT a hardcoded English literal in the durable row.
    /// </summary>
    public const string DefaultNameKey = "calendar.defaultName";

    // ---- Identity + tenancy ------------------------------------------------

    /// <summary>The owning-calendar id (UUIDv7). Part of the composite <c>(TenantId, Id)</c> store key.</summary>
    public CalendarId Id { get; private set; }

    /// <summary>The owning tenant. Part of the composite key — a foreign-tenant read returns null.</summary>
    public TenantId TenantId { get; private set; }

    // ---- Collection identity ----------------------------------------------

    /// <summary>
    /// The calendar's display name. For a user-created calendar this is free-text user data stored
    /// as-is. For the provisioned default it is the i18n key <see cref="DefaultNameKey"/> (see the
    /// remarks / <see cref="IsDefault"/>).
    /// </summary>
    public string Name { get; private set; }

    /// <summary>Which kind of calendar this is (personal / team / resource) — design §1.5.</summary>
    public CalendarKind Kind { get; private set; }

    /// <summary>
    /// The design-TOKEN name for this calendar's colour swatch (e.g. <c>calendar-accent-3</c>), never a
    /// hex literal (design §2.3 — the picker assigns the next unused token on create so the legend stays
    /// theme-aware + WCAG-AA in light and dark automatically). <see langword="null"/> until assigned.
    /// </summary>
    public string? ColorToken { get; private set; }

    /// <summary>
    /// The principal who owns this calendar (audit + later visibility). For the single-founder MVP this
    /// is the founder principal; per-principal ownership binds when multi-user enrollment (#118) lands.
    /// </summary>
    public Guid OwnerActorId { get; private set; }

    /// <summary>
    /// The party/asset this calendar is a lens over — set ONLY for <see cref="CalendarKind.Resource"/>
    /// (a doctor's time, a room). <see langword="null"/> for a Personal / Team calendar.
    /// </summary>
    public ParticipantRef? ResourceRef { get; private set; }

    /// <summary>
    /// True for the single provisioned default calendar per tenant (design §1.4). A default calendar's
    /// <see cref="Name"/> is the i18n key <see cref="DefaultNameKey"/>; a renamed default keeps
    /// <see cref="IsDefault"/> but carries a free-text name thereafter.
    /// </summary>
    public bool IsDefault { get; private set; }

    // ---- Audit -------------------------------------------------------------

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public long Version { get; private set; }

    // ---- Construction ------------------------------------------------------

    private OwnedCalendar(
        CalendarId id,
        TenantId tenantId,
        string name,
        CalendarKind kind,
        Guid ownerActorId,
        ParticipantRef? resourceRef,
        bool isDefault,
        string? colorToken,
        DateTimeOffset createdAt,
        Guid createdBy)
    {
        Id           = id;
        TenantId     = tenantId;
        Name         = name;
        Kind         = kind;
        OwnerActorId = ownerActorId;
        ResourceRef  = resourceRef;
        IsDefault    = isDefault;
        ColorToken   = colorToken;
        CreatedAt    = createdAt;
        UpdatedAt    = createdAt;
        CreatedBy    = createdBy;
        UpdatedBy    = createdBy;
        Version      = 0;
    }

    /// <summary>
    /// Create a new owned calendar. A <see cref="CalendarKind.Resource"/> calendar REQUIRES a
    /// <paramref name="resourceRef"/> (the party/asset it views); a Personal / Team calendar must NOT
    /// carry one. The name must be non-empty free-text (user data). Use <see cref="CreateDefault"/> for
    /// the provisioned default calendar (whose name is the i18n key, not free-text).
    /// </summary>
    public static OwnedCalendar Create(
        TenantId tenantId,
        string name,
        CalendarKind kind,
        Guid ownerActorId,
        ParticipantRef? resourceRef = null,
        string? colorToken = null,
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Calendar name must be non-empty.", nameof(name));
        ValidateKindResource(kind, resourceRef);

        return new OwnedCalendar(
            id:           CalendarId.NewId(),
            tenantId:     tenantId,
            name:         name.Trim(),
            kind:         kind,
            ownerActorId: ownerActorId,
            resourceRef:  resourceRef,
            isDefault:    false,
            colorToken:   NormalizeColorToken(colorToken),
            createdAt:    createdAt ?? DateTimeOffset.UtcNow,
            createdBy:    ownerActorId);
    }

    /// <summary>
    /// Create the provisioned DEFAULT personal calendar for a tenant (design §1.4). Its
    /// <see cref="Name"/> is the i18n key <see cref="DefaultNameKey"/> (resolved to a localized "My
    /// calendar" by the Harborline App in the viewer's locale — never a hardcoded English seed literal, §1.6),
    /// its <see cref="Kind"/> is <see cref="CalendarKind.Personal"/>, and <see cref="IsDefault"/> is
    /// true. Provisioning is idempotent at the store layer (provision only when the tenant has zero
    /// calendars).
    /// </summary>
    public static OwnedCalendar CreateDefault(
        TenantId tenantId,
        Guid ownerActorId,
        string? colorToken = null,
        DateTimeOffset? createdAt = null)
        => new(
            id:           CalendarId.NewId(),
            tenantId:     tenantId,
            name:         DefaultNameKey,
            kind:         CalendarKind.Personal,
            ownerActorId: ownerActorId,
            resourceRef:  null,
            isDefault:    true,
            colorToken:   NormalizeColorToken(colorToken),
            createdAt:    createdAt ?? DateTimeOffset.UtcNow,
            createdBy:    ownerActorId);

    // ---- Mutators ----------------------------------------------------------

    /// <summary>
    /// Rename the calendar to a free-text name (user data). A default calendar keeps
    /// <see cref="IsDefault"/> but its name becomes free-text thereafter (it is no longer the i18n key).
    /// </summary>
    public void Rename(string name, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Calendar name must be non-empty.", nameof(name));
        Name = name.Trim();
        Stamp(updatedBy, updatedAt);
    }

    /// <summary>Set (or clear, with <see langword="null"/>) the design-token colour swatch name.</summary>
    public void SetColorToken(string? colorToken, Guid updatedBy, DateTimeOffset? updatedAt = null)
    {
        ColorToken = NormalizeColorToken(colorToken);
        Stamp(updatedBy, updatedAt);
    }

    // ---- Rehydration (store round-trip) -----------------------------------

    /// <summary>
    /// Rehydrate an <see cref="OwnedCalendar"/> from persisted state — the store's only reconstruction
    /// path (mirrors <see cref="CalendarEvent.Rehydrate"/>). Does not re-validate business rules the
    /// create factory enforced; it faithfully restores the stored row.
    /// </summary>
    public static OwnedCalendar Rehydrate(
        CalendarId id,
        TenantId tenantId,
        string name,
        CalendarKind kind,
        Guid ownerActorId,
        ParticipantRef? resourceRef,
        bool isDefault,
        string? colorToken,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        Guid createdBy,
        Guid updatedBy,
        long version)
        => new(id, tenantId, name, kind, ownerActorId, resourceRef, isDefault, colorToken, createdAt, createdBy)
        {
            UpdatedAt = updatedAt,
            UpdatedBy = updatedBy,
            Version   = version,
        };

    // ---- Helpers -----------------------------------------------------------

    private static void ValidateKindResource(CalendarKind kind, ParticipantRef? resourceRef)
    {
        if (kind == CalendarKind.Resource && resourceRef is null)
            throw new ArgumentException(
                "A Resource calendar requires a ResourceRef (the party/asset it is a lens over).",
                nameof(resourceRef));
        if (kind != CalendarKind.Resource && resourceRef is not null)
            throw new ArgumentException(
                $"A {kind} calendar must not carry a ResourceRef (only a Resource calendar is a lens over a party/asset).",
                nameof(resourceRef));
    }

    private static string? NormalizeColorToken(string? colorToken)
        => string.IsNullOrWhiteSpace(colorToken) ? null : colorToken.Trim();

    private void Stamp(Guid updatedBy, DateTimeOffset? updatedAt)
    {
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow;
        Version  += 1;
    }
}
