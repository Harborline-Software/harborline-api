using Harborline.Api.Blocks.Docs.Models;
using Harborline.Api.Blocks.Docs.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Docs;

/// <summary>
/// Node-side <see cref="IMimeTypeAndSizePolicy"/> that enforces the inline-tier
/// ceiling on top of the shared three-gate <see cref="MimeTypeAndSizePolicy"/>
/// (ADR 0127 — documents storage tier for the local-first node, SEC-1 + the
/// 25&#160;MB inline ceiling).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a node-local decorator and not an edit to the shared policy.</b> The
/// shared <see cref="MimeTypeAndSizePolicy"/> is consumed by BOTH the Bridge
/// (Npgsql) and the node (SQLite/SQLCipher). On the node, document bytes live
/// <i>inline</i> in <c>local-node.db</c> via <c>StorageRef.ForInline</c>, so an
/// over-large upload is a real memory-pressure / DoS vector and must be
/// rejected — the deferred out-of-line blob tier that would catch it does not
/// exist yet (ADR 0127 §"Inline size ceiling"). The Bridge has no such inline
/// ceiling and inlines everything up to the 100&#160;MB
/// <see cref="BlocksDocsOptions.MaxAttachmentBytes"/> hard cap. Adding the
/// inline gate to the shared policy would silently start rejecting Bridge
/// uploads &gt;&#160;<see cref="BlocksDocsOptions.InlineBlobMaxBytes"/> (default
/// 8&#160;KB) and break the Bridge documents path + its tests + C2 parity. So the
/// gate lives HERE, in the node composition only, wrapping the unchanged shared
/// policy.
/// </para>
/// <para>
/// <b>Gate order.</b> The shared policy runs first (system blacklist → per-tenant
/// MIME whitelist → outer <see cref="BlocksDocsOptions.MaxAttachmentBytes"/> cap →
/// tenant quota). Only on an accept does this decorator apply the inline ceiling.
/// In v1 inline is the ONLY tier, so the two size knobs are effectively in series:
/// the outer cap (100&#160;MB) and the inline ceiling (25&#160;MB) — the inline
/// ceiling is the binding one, but they are <i>separately expressed</i> so the
/// future out-of-line <c>FoundationBlob</c> tier can raise the outer cap above the
/// inline ceiling without un-conflating a single knob (ADR 0127 council RULING B).
/// </para>
/// <para>
/// <b>SEC-1 fail-closed.</b> This is a CONCRETE policy; the node composition
/// (<see cref="NodeDocsWriteComposition.AddNodeDocsWrites"/>) wires it as the
/// <see cref="IMimeTypeAndSizePolicy"/> and constructs <see cref="AttachmentService"/>
/// with a NON-null policy. A null/absent policy can never ship — the composition
/// throws at startup if the ceiling is non-positive, and the
/// <see cref="AttachmentService"/> is built with this instance, so the
/// <c>if (_policy is not null)</c> short-circuit in <c>ApplyGatesAsync</c> always
/// takes the enforce branch on the node.
/// </para>
/// </remarks>
public sealed class NodeInlineCeilingPolicy : IMimeTypeAndSizePolicy
{
    private readonly IMimeTypeAndSizePolicy _inner;
    private readonly long _inlineMaxBytes;

    /// <summary>
    /// Construct over the shared three-gate policy + the enforced inline ceiling
    /// (<paramref name="inlineMaxBytes"/>, read from
    /// <see cref="BlocksDocsOptions.InlineBlobMaxBytes"/>). Throws when the ceiling
    /// is non-positive — a misconfigured ceiling MUST fail closed at composition
    /// time, never silently disable the gate.
    /// </summary>
    public NodeInlineCeilingPolicy(IMimeTypeAndSizePolicy inner, long inlineMaxBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (inlineMaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inlineMaxBytes),
                inlineMaxBytes,
                "The node inline-tier ceiling (InlineBlobMaxBytes) must be a positive byte count. " +
                "A zero/negative ceiling would disable the enforced inline gate (ADR 0127 SEC-1 fail-closed).");
        }
        _inlineMaxBytes = inlineMaxBytes;
    }

    /// <inheritdoc />
    public async Task<PolicyResult> ValidateAsync(
        TenantId tenantId,
        string sniffedMime,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        // Shared gates first (MIME blacklist/whitelist → outer cap → tenant quota).
        var shared = await _inner.ValidateAsync(tenantId, sniffedMime, sizeBytes, cancellationToken)
            .ConfigureAwait(false);
        if (shared.Rejected)
        {
            return shared;
        }

        // Inline-tier ceiling (ADR 0127). On the node, inline is the only storage
        // tier, so an above-ceiling upload is rejected outright — the deferred
        // out-of-line blob tier that would catch it does not exist yet. The detail
        // string is PII-free (raw byte counts only — no filename, no tenant id) and
        // ACTIONABLE for the end user.
        if (sizeBytes > _inlineMaxBytes)
        {
            var limitMb = _inlineMaxBytes / (1024.0 * 1024.0);
            return PolicyResult.Reject(
                PolicyRejection.InlineSize,
                $"Document exceeds the {limitMb:0.#} MB single-file limit; split it or reduce scan resolution.");
        }

        return PolicyResult.Accept();
    }
}
