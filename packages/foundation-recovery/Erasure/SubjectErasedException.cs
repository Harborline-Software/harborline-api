using System;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// Thrown by the per-subject key-derivation path when the requested subject has
/// been crypto-shredded (ADR 0135 GDPR direction). The wrapping sub-key no longer
/// exists — by design — so any operation that needs it (decrypt, re-encrypt)
/// fails closed. Callers on the decrypt path surface this as a denial, never as
/// plaintext.
/// </summary>
public sealed class SubjectErasedException : Exception
{
    /// <summary>Construct the exception for the named tenant + subject.</summary>
    public SubjectErasedException(string tenant, string subject)
        : base($"Data subject '{subject}' has been crypto-shredded for tenant '{tenant}'; "
               + "the per-subject key is permanently destroyed and the ciphertext is undecryptable.")
    {
        Tenant = tenant;
        Subject = subject;
    }

    /// <summary>The tenant the erasure was recorded under.</summary>
    public string Tenant { get; }

    /// <summary>The erased data subject.</summary>
    public string Subject { get; }
}
