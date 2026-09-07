using System.Text;
using System.Text.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 274 — ActorId has ONE canonical form (non-empty, unpadded, Unicode form C), refused at every
/// trust boundary and canonicalised only where an id is minted from a shell value. Case is preserved but
/// the OS-user segment of an <c>os:{user}#{key}</c> party id compares case-insensitively, because that
/// segment is an operating-system account name.
/// </summary>
public sealed class ActorIdCanonicalFormTests
{
    private const string Canonical = "os:alice#deadbeef";

    /// <summary>Padded, empty and decomposed spellings — the non-canonical inputs every boundary refuses.</summary>
    public static TheoryData<string> NonCanonical() => new()
    {
        " os:alice#deadbeef",
        "os:alice#deadbeef ",
        "\tos:alice#deadbeef\n",
        "",
        "   ",
        // "jose+combining acute" written with a combining acute (form D) — a second byte spelling of one principal.
        "os:jose\u0301#deadbeef",
    };

    [Theory]
    [MemberData(nameof(NonCanonical))]
    public void TheTypeRefusesANonCanonicalValue(string raw)
    {
        Assert.Throws<ArgumentException>(() => new ActorId(raw));
    }

    [Fact]
    public void TheTypeAcceptsAnAlreadyCanonicalValue()
    {
        Assert.Equal(Canonical, new ActorId(Canonical).Value);
        Assert.Equal("os:jos\u00e9#deadbeef", new ActorId("os:jos\u00e9#deadbeef").Value);
    }

    // ---- boundary 1: the wire (deserialisation) -------------------------------------------------

    [Theory]
    [MemberData(nameof(NonCanonical))]
    public void TheWireRefusesANonCanonicalActorId(string raw)
    {
        var json = JsonSerializer.Serialize(raw);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ActorId>(json));
    }

    [Fact]
    public void TheWireRoundTripsTheCanonicalForm()
    {
        var json = JsonSerializer.Serialize(new ActorId(Canonical));
        Assert.Equal("\"os:alice#deadbeef\"", json);
        Assert.Equal(new ActorId(Canonical), JsonSerializer.Deserialize<ActorId>(json));
    }

    // ---- boundary 2: the grant store ----------------------------------------------------------

    [Theory]
    [MemberData(nameof(NonCanonical))]
    public void TheGrantStoreRefusesANonCanonicalSubject(string raw)
    {
        Assert.Throws<ArgumentException>(() => Grant(new ActorId(raw)));
    }

    // ---- boundary 3: the audit append ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(NonCanonical))]
    public void TheAuditAppendRefusesANonCanonicalActor(string raw)
    {
        Assert.Throws<ArgumentException>(() => new AuditRecord(
            Guid.NewGuid(),
            TenantId.FromString("tenant-a"),
            AuditEventType.SecurityPolicyProposed,
            DateTimeOffset.UnixEpoch,
            null!,
            Array.Empty<AttestingSignature>(),
            Actor: new ActorId(raw)));
    }

    // ---- the ONE minting site ------------------------------------------------------------------

    [Fact]
    public void TwoSpellingsMintedFromTheShellAreOneId()
    {
        var padded = ActorId.Mint("  os:alice#deadbeef\t");
        var decomposed = ActorId.Mint("os:jose\u0301#deadbeef");

        Assert.Equal(new ActorId(Canonical), padded);
        Assert.Equal(new ActorId("os:jos\u00e9#deadbeef"), decomposed);
        Assert.Equal(padded.GetHashCode(), new ActorId(Canonical).GetHashCode());
    }

    [Fact]
    public void TheOsUserSegmentComparesTheWayTheOperatingSystemComparesIt()
    {
        var lower = new ActorId("os:alice#deadbeef");
        var upper = new ActorId("os:Alice#deadbeef");

        Assert.Equal(lower, upper);                              // one account, two spellings
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.Equal("os:Alice#deadbeef", upper.Value);          // and the spelling is PRESERVED
        Assert.True(ActorId.SameActor("os:alice#deadbeef", "os:ALICE#deadbeef"));

        // The opaque key suffix, and every non-OS id, stay case-SENSITIVE.
        Assert.NotEqual(lower, new ActorId("os:alice#DEADBEEF"));
        Assert.NotEqual(new ActorId("party:preparer"), new ActorId("party:Preparer"));
    }

    private static AccessGrant Grant(ActorId principal)
    {
        var at = DateTimeOffset.UnixEpoch;
        return new AccessGrant(
            GrantId.New(), TenantId.FromString("tenant-a"), principal, RoleReference.Administrator,
            ScopeExpression.Parse("/"), GrantResidency.Cache, new GrantValidity(at, at.AddHours(2)),
            GranterKind.Person, ActorId.System, at,
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), ActorId.System),
            at);
    }
}
