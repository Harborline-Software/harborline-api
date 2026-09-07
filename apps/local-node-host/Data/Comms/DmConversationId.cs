using System.Security.Cryptography;
using System.Text;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// Derives the deterministic <c>dm:&lt;hash&gt;</c> conversation id for a 1:1 direct message (C2) from the
/// UNORDERED pair of participant party ids, salted by the team id. The load-bearing property: both ends
/// compute the SAME id with ZERO coordination, so a DM "just works" the first time either side sends — there
/// is no create-handshake (design §1.3, the no-coordination property).
/// </summary>
/// <remarks>
/// <para>
/// <b>The derivation (design §1.3):</b>
/// <code>
/// conversationId(dm) = "dm:" + base32( SHA-256( teamId || "\0" || sort(partyA, partyB).join("\0") ) )[:26]
/// </code>
/// <list type="bullet">
///   <item><b>Unordered</b> (<c>sort(...)</c>) → A→B and B→A yield the SAME id. This is the no-coordination
///     property: each side independently sorts the pair, so both compute identical bytes.</item>
///   <item><b>Team-scoped in the hash</b> (<c>teamId ||</c>) → the same two people in two different orgs get
///     two distinct DM threads (the org-isolation invariant; a DM is <em>within</em> a team's roster).</item>
///   <item><b>Hash, not concatenation</b> → fixed-length, opaque; no party id leaks in the id itself (the id
///     appears on the wire as a sync <c>StreamId</c>; a non-participant who observes stream ids learns only
///     "some DM exists," not who).</item>
///   <item><b><c>"dm:"</c> prefix</b> distinguishes DM streams from <c>"team"</c> / <c>"contacts"</c> for the
///     router + the (later C5) participant filter, and lets <see cref="CommsConversation.IsDirectMessage"/>
///     classify the id. Group / channel get <c>"grp:"</c> / <c>"chan:"</c> later.</item>
/// </list>
/// </para>
/// <para>
/// <b>Separator discipline.</b> A NUL (<c>\0</c>) joins the salt + the two sorted party ids so that
/// <c>("ab", "c")</c> and <c>("a", "bc")</c> hash distinctly (no concatenation-ambiguity). Party ids are
/// UTF-8 encoded. The hash is the full SHA-256 digest; the id keeps the first 26 base32 chars (130 bits) —
/// ample collision resistance for a local office's DM set while keeping the id compact.
/// </para>
/// <para>
/// <b>Sealed + shipped (C4 + C5).</b> The id derivation lives here; C4 sealed the body (content encryption) and
/// C5 made the keys roster-bound + node-secret and scoped the sync to participants. The DM route surface now
/// SHIPS, gated by the <see cref="CommsDmFeatureFlag"/> (default ON, kill-switch). The <b>identity</b> derived
/// here is final; C4/C5 added the seal + participant-scoped routing on top, keyed off the same <c>dm:</c> id.
/// </para>
/// <para>
/// <b>Forge-proof unchanged.</b> The id is in the SIGNED payload (C1 added <c>ConversationId</c> to
/// <see cref="MessageCrdtState.SignablePayload"/>), so a DM message cannot be replayed into another thread —
/// the deterministic id composes with the existing per-author signature gate orthogonally.
/// </para>
/// </remarks>
public static class DmConversationId
{
    /// <summary>
    /// The number of base32 characters of the SHA-256 digest the <c>dm:</c> id keeps (130 bits of entropy —
    /// compact + ample collision resistance for a local office's DM set).
    /// </summary>
    public const int HashLength = 26;

    private const byte Separator = 0x00; // NUL joins salt + the two sorted party ids (no concat-ambiguity).

    /// <summary>
    /// Derive the deterministic 1:1 DM conversation id for the UNORDERED pair (<paramref name="partyA"/>,
    /// <paramref name="partyB"/>) within team <paramref name="teamId"/>. Order-independent:
    /// <c>Derive(t, a, b) == Derive(t, b, a)</c>. The two parties must be distinct (a self-DM is not a 1:1
    /// conversation).
    /// </summary>
    /// <param name="teamId">The team/tenant the DM is within (salts the hash → distinct threads per org).</param>
    /// <param name="partyA">One participant's party id.</param>
    /// <param name="partyB">The other participant's party id.</param>
    /// <returns>The <c>dm:&lt;base32&gt;</c> conversation id both participants independently compute.</returns>
    /// <exception cref="ArgumentException">A null/empty argument, or the two party ids are equal (a self-DM).</exception>
    public static string Derive(string teamId, string partyA, string partyB)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(partyA);
        ArgumentException.ThrowIfNullOrWhiteSpace(partyB);

        if (string.Equals(partyA, partyB, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A 1:1 DM conversation requires two DISTINCT participants — a self-DM is not a 1:1 conversation.",
                nameof(partyB));
        }

        // UNORDERED: sort the pair with Ordinal so both ends agree on the byte layout regardless of who derives.
        var (first, second) = string.CompareOrdinal(partyA, partyB) <= 0 ? (partyA, partyB) : (partyB, partyA);

        // teamId \0 first \0 second  → SHA-256 → base32[:26].
        var buffer = new MemoryStream();
        WriteUtf8(buffer, teamId);
        buffer.WriteByte(Separator);
        WriteUtf8(buffer, first);
        buffer.WriteByte(Separator);
        WriteUtf8(buffer, second);

        var digest = SHA256.HashData(buffer.ToArray());
        var hash = Base32.Encode(digest)[..HashLength];
        return CommsConversation.DirectMessagePrefix + hash;
    }

    private static void WriteUtf8(MemoryStream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Crockford-style lowercase base32 (no padding), used to render the SHA-256 digest as the opaque, URL- and
    /// stream-id-safe <c>dm:</c> hash. Deterministic + dependency-free (a self-contained encoder keeps the
    /// derivation reproducible on every node without a shared base32 library).
    /// </summary>
    private static class Base32
    {
        // Crockford base32 alphabet (lowercase), excludes i, l, o, u to avoid ambiguity.
        private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

        public static string Encode(ReadOnlySpan<byte> data)
        {
            var output = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bitsLeft = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bitsLeft += 8;
                while (bitsLeft >= 5)
                {
                    bitsLeft -= 5;
                    output.Append(Alphabet[(buffer >> bitsLeft) & 0x1F]);
                }
            }
            if (bitsLeft > 0)
            {
                output.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
            }
            return output.ToString();
        }
    }
}
