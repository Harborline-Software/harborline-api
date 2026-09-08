using System;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.LocalNodeHost.Data.Forms;

/// <summary>
/// Fail-closed null-object <see cref="ISubjectFieldEncryptor"/> for the node-forms
/// governance composition (F-11). The SPINE-2 <c>FieldPolicyEnforcer</c> requires this
/// service in its constructor, but the FORM value path never invokes it: a subject-scoped
/// class (phi / identifier) is refused earlier by the PEP because the ADR 0139 D3
/// <c>subjectRef</c> primary-subject seam is not yet wired (the form engine passes a null
/// subject). This default lets the enforcer construct in any node-forms composition while
/// keeping the subject-DEK path fail-closed; a host that wires
/// <c>AddHarborlineRecoveryCoordinator</c> FIRST supplies the real
/// <see cref="SubjectKeyFieldEncryptor"/> (TryAdd keeps it). Fail-closed by construction —
/// it derives NO key material, mirroring the no-mock-crypto-resolver rule.
/// </summary>
internal sealed class FailClosedSubjectFieldEncryptor : ISubjectFieldEncryptor
{
    public Task<EncryptedField> EncryptForSubjectAsync(
        ReadOnlyMemory<byte> plaintext, TenantId tenant, SubjectId subject, CancellationToken ct)
        => throw new InvalidOperationException(
            "Per-subject field encryption is not available on this node: the ADR 0139 D3 subjectRef " +
            "primary-subject seam is not wired. A subject-scoped classification (phi / identifier) cannot " +
            "be stored here. Wire AddHarborlineRecoveryCoordinator (and the subjectRef seam) to enable it.");
}

/// <summary>
/// Fail-closed null-object <see cref="ISubjectErasureService"/> for the node-forms governance
/// composition (F-11). The <c>FieldPolicyEnforcer</c> requires it in its constructor, but the
/// FORM save/read path never invokes it (crypto-shred is the EraseSubject PEP, a separate flow).
/// A host that wires <c>AddHarborlineRecoveryCoordinator</c> + the durable erasure stores FIRST
/// supplies the real <c>SubjectErasureService</c> (TryAdd keeps it). Fail-closed by construction.
/// </summary>
internal sealed class FailClosedSubjectErasureService : ISubjectErasureService
{
    public Task<SubjectErasureResult> EraseAsync(SubjectErasureRequest request, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "Subject erasure (crypto-shred) is not available on this node-forms composition. Wire " +
            "AddHarborlineRecoveryCoordinator with durable erasure stores (RequireDurableErasureStores) to enable it.");
}
