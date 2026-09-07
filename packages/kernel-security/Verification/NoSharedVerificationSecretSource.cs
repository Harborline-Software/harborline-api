namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// The fail-closed null-object <see cref="ISharedVerificationSecretSource"/> — always returns <c>null</c>
/// (ADR 0152 §Item-(c), the no-mock-crypto B1 pattern; mirrors ADR 0136 <c>NoDmConversationKeyProvider</c>).
/// </summary>
/// <remarks>
/// This is the PRODUCTION default registered by <c>AddHarborlineKernelSecurity</c>. A minimal graph with no
/// authenticated engagement context therefore degrades to "verification unavailable" — never to a
/// derivable stand-in. A consumer that holds a real authenticated context (a completed ADR 0076 handshake
/// or a purpose-signed co-roster ECDH) constructs <see cref="HandshakeTranscriptSecretSource"/> /
/// <see cref="RosterEcdhSecretSource"/> explicitly with the runtime secret material and overrides this
/// registration for its scope. Any identity-derivable stand-in lives ONLY in the test assembly and is
/// arch-fenced out of the shipped assembly.
/// </remarks>
public sealed class NoSharedVerificationSecretSource : ISharedVerificationSecretSource
{
    /// <inheritdoc />
    public VerificationSecret? TryGetSharedSecret(VerificationContext ctx) => null;
}
