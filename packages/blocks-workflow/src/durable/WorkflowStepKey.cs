using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// The per-<c>(instance, iteration, step)</c> idempotency key — the durable proof that "this step
/// already advanced", co-committed with the step's effect (ADR 0135 D2, <b>build invariant #2</b>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a value type, not a bare string.</b> The idempotency key is the single load-bearing dedup
/// token for the whole engine: its presence in <c>workflow_step_idempotency</c> turns a redelivered
/// trigger (or a post-crash resume) into a no-op. ADR 0135 D2 invariant #2 requires it be derived from
/// <c>(instance, iteration, step)</c> and <b>never</b> from a freshly-minted entity id — the exact
/// failure mode of bug-1337, where <c>NodeEfRecurringInvoiceService</c> minted a new <c>draftId</c> on
/// resume so the derived <c>SourceReference</c> differed and dedup broke.
/// </para>
/// <para>
/// <b>Cross-architecture byte-stability (build invariant #2, second half).</b> In the planned
/// multi-machine tenant (ADR 0135 D3 — an offline client + the home node, possibly Mac ARM64 + Windows
/// x64) the SAME <c>(instance, iteration, step)</c> must derive a <b>byte-identical</b> key + derived
/// <c>SourceReference</c> on either architecture, or dedup breaks across the tenant and bug-1337
/// re-opens. <see cref="ToDeterministicGuid"/> therefore stamps the RFC-4122 version/variant on the
/// <b>big-endian</b> byte positions and assembles the <see cref="Guid"/> with an explicit big-endian
/// read (<see cref="BinaryPrimitives"/>, NOT the host-endianness <c>BitConverter</c> + mixed-endian
/// <c>new Guid(int, short, short, …)</c> ctor). This is the same vetted technique as the merged bug-1337
/// follow-up's <c>NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId</c> and the comms block's
/// <c>RFC4122GuidFormatter.ReadBigEndian</c> (kept inline here so <c>blocks-workflow</c> takes no
/// dependency on either for a 16-byte conversion).
/// </para>
/// </remarks>
public readonly record struct WorkflowStepKey
{
    /// <summary>The instance this step belongs to.</summary>
    public string InstanceId { get; }

    /// <summary>
    /// The 0-based iteration of the step within the instance. A step that runs once has iteration 0;
    /// a loop / retry that re-enters the same step bumps the iteration so each pass gets a distinct key.
    /// </summary>
    public int Iteration { get; }

    /// <summary>The step identifier within the definition (e.g. <c>"post"</c>, <c>"approve"</c>).</summary>
    public string Step { get; }

    /// <summary>Constructs a step key. All three components participate in the derived value.</summary>
    /// <exception cref="ArgumentException">If <paramref name="instanceId"/> or <paramref name="step"/> is null/empty, or <paramref name="iteration"/> is negative.</exception>
    public WorkflowStepKey(string instanceId, int iteration, string step)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentException.ThrowIfNullOrEmpty(step);
        ArgumentOutOfRangeException.ThrowIfNegative(iteration);
        InstanceId = instanceId;
        Iteration = iteration;
        Step = step;
    }

    /// <summary>
    /// The canonical, culture-invariant string form — <c>"{instance}:{iteration}:{step}"</c>. This is the
    /// primary-key value stored in <c>workflow_step_idempotency</c>. Stable across runs and machines:
    /// it is a pure function of the three components, with no clock, GUID, or culture input.
    /// </summary>
    public string Value => string.Create(
        CultureInfo.InvariantCulture,
        $"{InstanceId}:{Iteration}:{Step}");

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// Derives a deterministic RFC-4122 <b>version-5</b> (name-based, SHA-256) UUID from this key, prefixed
    /// by <paramref name="purpose"/>. Use this for any downstream deterministic id the step needs — e.g. a
    /// derived <c>SourceReference</c> for the posting effect — so a re-run yields the IDENTICAL id and the
    /// JE-layer <c>ux_journal_entries_tenant_source_ref</c> unique index is a deterministic second backstop
    /// (independent of the atomic-commit guarantee).
    /// </summary>
    /// <param name="purpose">
    /// A namespacing discriminator so distinct derived ids for the same step never collide (e.g.
    /// <c>"source-reference"</c> vs <c>"draft-id"</c>). Required.
    /// </param>
    /// <returns>A reproducible, cross-architecture-byte-stable <see cref="Guid"/>.</returns>
    public Guid ToDeterministicGuid(string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        return DeriveV5Guid($"{purpose}|{Value}");
    }

    /// <summary>
    /// The shared cross-architecture-byte-stable version-5 GUID derivation. Public + static so the EF store
    /// impl (and the determinism arch-tests) can re-derive against the SAME algorithm without duplicating
    /// the big-endian assembly.
    /// </summary>
    /// <param name="name">The name-string hashed into the UUID. Caller-namespaced.</param>
    public static Guid DeriveV5Guid(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));

        // First 16 hash bytes as the RFC-4122 big-endian layout; stamp version (5) + variant in place
        // (these byte positions are byte-order-independent).
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC-4122 variant

        // Assemble the Guid from the big-endian layout with explicit fixed byte order (deterministic across
        // CPU architectures). Guid's in-memory layout is mixed-endian (Data1/2/3 little-endian, Data4
        // big-endian), so reverse the first three groups when reading the big-endian source — exactly the
        // bug-1337 follow-up's RFC4122GuidFormatter.ReadBigEndian conversion.
        Span<byte> ms = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(ms[..4], BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]));
        BinaryPrimitives.WriteUInt16LittleEndian(ms.Slice(4, 2), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(ms.Slice(6, 2), BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6, 2)));
        bytes.Slice(8, 8).CopyTo(ms.Slice(8, 8));

        return new Guid(ms);
    }
}
