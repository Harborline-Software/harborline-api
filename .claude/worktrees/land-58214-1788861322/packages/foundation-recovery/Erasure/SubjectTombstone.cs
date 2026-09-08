using System;
using System.Collections.Generic;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// The pseudonymized record left in place of a crypto-shredded data subject's
/// identifying fields (ADR 0135 GDPR direction). The tombstone preserves
/// referential continuity — rows that pointed at the subject still resolve — while
/// carrying <em>no</em> identifying data: the subject's <see cref="EncryptedField"/>
/// ciphertext is already undecryptable (its key is destroyed), and the cleartext
/// identifying fields are replaced by a stable, non-reversible pseudonym.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pseudonym derivation.</b> The <see cref="Pseudonym"/> is a deterministic,
/// non-reversible label (a salted hash of the tenant + subject id) so the same
/// subject always tombstones to the same pseudonym (idempotent re-erasure;
/// stable cross-reference) WITHOUT the pseudonym revealing the original
/// identifier. It is NOT derived from any erased plaintext.
/// </para>
/// <para>
/// <b>Append-only.</b> A tombstone is written once at erasure and never mutated
/// or removed — the same one-directional discipline as the crypto-shred itself.
/// </para>
/// </remarks>
/// <param name="TenantId">The tenant the erased subject belonged to.</param>
/// <param name="Pseudonym">The non-reversible pseudonymous label that replaces the subject's identifying fields.</param>
/// <param name="ErasedAt">When the erasure took effect.</param>
/// <param name="ApprovingActors">The multi-actor approval chain that authorized the erasure (ADR 0068 §3 floor), recorded for the compliance audit. Never empty for a real erasure.</param>
/// <param name="LegalBasis">Free-text legal-determination reference the deployer recorded (e.g. an erasure-request ticket id). Opaque to the platform per ADR 0068 §GC.1 — Harborline does not make the legal determination.</param>
public sealed record SubjectTombstone(
    TenantId TenantId,
    string Pseudonym,
    DateTimeOffset ErasedAt,
    IReadOnlyList<ActorId> ApprovingActors,
    string LegalBasis);
