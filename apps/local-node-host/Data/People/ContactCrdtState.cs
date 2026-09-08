using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.People;

/// <summary>
/// The synced projection of a contact's mutable core fields — the value stored, per contact id, in the
/// contacts CRDT map (multi-device INC-4). This is deliberately a flat serializable snapshot, NOT the EF
/// <see cref="Party"/> aggregate: it carries exactly the fields the contacts grid/detail surfaces and that
/// converge across replicas. The CRDT map keys this record by <see cref="ContactId"/>, so YDotNet's
/// per-key map LWW resolves concurrent edits to the SAME contact, while edits to DIFFERENT contacts both
/// land (the CRDT property the acceptance test proves).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope fence (v1 spike).</b> This projects the core <see cref="Party"/> mutable fields only. The
/// append-only contact sub-collections (emails / phones / addresses / roles) are deliberately NOT synced in
/// this increment — they are append-only history rows that need their own per-row CRDT list projection,
/// which is the larger follow-on flagged to Admiral/CIC. The contacts grid + the create/update surfaces
/// operate entirely on the core fields below, so convergence of those fields is the demonstrable spike.
/// </para>
/// <para>
/// <b>Tombstone-not-delete.</b> A delete is represented by <see cref="Deleted"/> = true with the tombstone
/// stamps preserved (the no-hard-DELETE doctrine + CRDT §4). The key is NOT removed from the map on delete;
/// it is set to a tombstoned state so the deletion converges to peers as a value, not as a key-removal that
/// a concurrent edit could resurrect.
/// </para>
/// </remarks>
public sealed record ContactCrdtState(
    string ContactId,
    string TenantId,
    string Kind,
    string DisplayName,
    string? LegalName,
    string? Notes,
    bool DoNotContact,
    bool DoNotEmail,
    bool DoNotCall,
    bool DoNotSms,
    bool Deleted,
    string CreatedAtIso,
    string CreatedBy,
    string UpdatedAtIso,
    string? UpdatedBy,
    long Version)
{
    /// <summary>Project a domain <see cref="Party"/> into its synced CRDT snapshot.</summary>
    public static ContactCrdtState FromParty(Party p) => new(
        ContactId:     p.Id.Value,
        TenantId:      p.TenantId.Value,
        Kind:          p.Kind.ToString(),
        DisplayName:   p.DisplayName,
        LegalName:     p.LegalName,
        Notes:         p.Notes,
        DoNotContact:  p.DoNotContact,
        DoNotEmail:    p.DoNotEmail,
        DoNotCall:     p.DoNotCall,
        DoNotSms:      p.DoNotSms,
        Deleted:       p.DeletedAt is not null,
        CreatedAtIso:  p.CreatedAt.ToString(),
        CreatedBy:     p.CreatedBy.Value,
        UpdatedAtIso:  p.UpdatedAt.ToString(),
        UpdatedBy:     p.UpdatedBy?.Value,
        Version:       p.Version);

    /// <summary>
    /// Rebuild a domain <see cref="Party"/> from a merged CRDT snapshot (inbound-delta reconcile path).
    /// Only the core mutable fields are carried; the person/organization detail fields that this spike
    /// does not sync are left at their defaults — the reconcile path preserves a locally-present row's
    /// un-synced fields by merging onto the existing row rather than overwriting it wholesale (see
    /// <c>ContactCrdtProjection.ReconcileAsync</c>).
    /// </summary>
    public Party ToParty()
    {
        var kind = Kind.Equals("Organization", System.StringComparison.OrdinalIgnoreCase)
            ? PartyKind.Organization
            : PartyKind.Person;

        return new Party
        {
            Id            = new PartyId(ContactId),
            TenantId      = new Harborline.Foundation.Assets.Common.TenantId(TenantId),
            Kind          = kind,
            DisplayName   = DisplayName,
            LegalName     = LegalName,
            Notes         = Notes,
            DoNotContact  = DoNotContact,
            DoNotEmail    = DoNotEmail,
            DoNotCall     = DoNotCall,
            DoNotSms      = DoNotSms,
            CreatedAt     = ParseInstant(CreatedAtIso),
            CreatedBy     = new PartyId(CreatedBy),
            UpdatedAt     = ParseInstant(UpdatedAtIso),
            UpdatedBy     = UpdatedBy is null ? (PartyId?)null : new PartyId(UpdatedBy),
            DeletedAt     = Deleted ? ParseInstant(UpdatedAtIso) : (Instant?)null,
            DeletedBy     = Deleted && UpdatedBy is not null ? new PartyId(UpdatedBy) : (PartyId?)null,
            Version       = Version,
        };
    }

    private static Instant ParseInstant(string iso) =>
        System.DateTimeOffset.TryParse(iso, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
            ? new Instant(dto)
            : throw new FormatException("The persisted contact instant is not valid ISO-8601.");
}
