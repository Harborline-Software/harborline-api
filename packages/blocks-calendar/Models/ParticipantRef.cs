namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// Which kind of fleet entity a <see cref="ParticipantRef"/> points at — a <b>Party</b> (a person
/// or organization: a doctor, a patient, a consultant, a team) or an <b>Asset</b> (a room, a piece
/// of equipment, a vehicle). The schedulable resource generalizes both
/// (capability-and-workflow-architecture.md §2.8.1 / schedule-feature design): a clinic books a
/// doctor (Party) and optionally a room (Asset); a conference room booking books a room (Asset); a
/// construction crew booking books staff (Party) and equipment (Asset).
/// </summary>
public enum ParticipantKind
{
    /// <summary>A <c>Harborline.Api.Blocks.People.Foundation.Models.PartyId</c> — a person or organization.</summary>
    Party = 0,

    /// <summary>A <c>Harborline.Api.Blocks.Assets.Domain.AssetId</c> — a room, equipment, or other bookable asset.</summary>
    Asset = 1,
}

/// <summary>
/// A discriminated reference to the entity a <see cref="CalendarParticipation"/> attaches to — a
/// <b>Party</b> (person / organization) or an <b>Asset</b> (room / equipment). The Slice S2
/// realization of the participant seam S0 shaped as an opaque <see cref="System.Guid"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a discriminated union and not a hard ref on the keystone blocks.</b> The canonical fleet
/// ids — <c>Harborline.Api.Blocks.People.Foundation.Models.PartyId</c> and
/// <c>Harborline.Api.Blocks.Assets.Domain.AssetId</c> — are both <b>string-backed</b> readonly record
/// structs with implicit string conversions, and their wire format is a stable string (the
/// party-model-convention §4 cross-cluster rule: clusters reference each other <i>by id value
/// only</i>, never by a hard <c>ProjectReference</c> on the other's domain block). Taking a
/// <c>ProjectReference</c> on <c>blocks-assets</c> in particular would drag its <c>ui-core</c> +
/// <c>ui-adapters-blazor</c> chain into this pure-domain block — the wrong dependency direction. So
/// <see cref="ParticipantRef"/> stores the canonical id's <b>string value</b> behind a typed
/// discriminator and exposes <see cref="Party(string)"/> / <see cref="Asset(string)"/> factories
/// that accept the canonical ids directly (via their implicit <c>string</c> operators) — reusing
/// the fleet types at the boundary without importing them. A consumer that <i>does</i> hold the
/// keystone types (the Bridge / a pack) round-trips losslessly: <c>ParticipantRef.Party(partyId)</c>
/// in, <c>new PartyId(ref.Value)</c> out.
/// </para>
/// <para>
/// <b>Closed union (exhaustive).</b> The two arms (<see cref="PartyRef"/> / <see cref="AssetRef"/>)
/// are the only ones — the private-protected base ctor seals the hierarchy so a <c>switch</c> over
/// <see cref="Kind"/> (or a pattern-match over the arms) is exhaustive, matching the fleet
/// <c>ImportOutcome</c> discriminated-union idiom.
/// </para>
/// </remarks>
public abstract record ParticipantRef
{
    private protected ParticipantRef(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Participant id value must be non-empty.", nameof(value));
        Value = value;
    }

    /// <summary>The discriminator — <see cref="ParticipantKind.Party"/> or <see cref="ParticipantKind.Asset"/>.</summary>
    public abstract ParticipantKind Kind { get; }

    /// <summary>
    /// The underlying canonical id's <b>string value</b> (a <c>PartyId.Value</c> or an
    /// <c>AssetId.Value</c>). The stable wire form per party-model-convention §4; resolve back to a
    /// typed id with <c>new PartyId(Value)</c> / <c>new AssetId(Value)</c> in a consumer that holds
    /// those types.
    /// </summary>
    public string Value { get; }

    /// <summary>True when this references a Party (person / organization).</summary>
    public bool IsParty => Kind == ParticipantKind.Party;

    /// <summary>True when this references an Asset (room / equipment).</summary>
    public bool IsAsset => Kind == ParticipantKind.Asset;

    /// <summary>
    /// Reference a <b>Party</b> by its id value. Accepts a
    /// <c>Harborline.Api.Blocks.People.Foundation.Models.PartyId</c> directly (it converts implicitly to
    /// <see cref="string"/>), so a caller that holds the keystone type writes
    /// <c>ParticipantRef.Party(partyId)</c>.
    /// </summary>
    public static ParticipantRef Party(string partyIdValue) => new PartyRef(partyIdValue);

    /// <summary>
    /// Reference an <b>Asset</b> by its id value. Accepts a
    /// <c>Harborline.Api.Blocks.Assets.Domain.AssetId</c> directly (implicit <see cref="string"/>
    /// conversion), so a caller writes <c>ParticipantRef.Asset(assetId)</c>.
    /// </summary>
    public static ParticipantRef Asset(string assetIdValue) => new AssetRef(assetIdValue);

    /// <summary>
    /// Exhaustively map this reference to a <typeparamref name="T"/> by its kind — the type-safe way
    /// to branch on Party-vs-Asset without an open <c>switch</c>.
    /// </summary>
    public T Match<T>(Func<string, T> onParty, Func<string, T> onAsset)
    {
        ArgumentNullException.ThrowIfNull(onParty);
        ArgumentNullException.ThrowIfNull(onAsset);
        return this switch
        {
            PartyRef p => onParty(p.Value),
            AssetRef a => onAsset(a.Value),
            _ => throw new InvalidOperationException($"Unhandled participant kind {Kind}."),
        };
    }

    public sealed override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Value}";

    /// <summary>A reference to a <b>Party</b> (person / organization).</summary>
    public sealed record PartyRef : ParticipantRef
    {
        public PartyRef(string partyIdValue) : base(partyIdValue) { }
        public override ParticipantKind Kind => ParticipantKind.Party;
    }

    /// <summary>A reference to an <b>Asset</b> (room / equipment).</summary>
    public sealed record AssetRef : ParticipantRef
    {
        public AssetRef(string assetIdValue) : base(assetIdValue) { }
        public override ParticipantKind Kind => ParticipantKind.Asset;
    }
}
