using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The node's CANONICAL principal signer — the local-node-host's
/// <c>RootSeedHex</c>→Ed25519 identity wired into the foundation
/// <see cref="IOperationSigner"/> (<see cref="CanonicalJson"/>/<see cref="SignedOperation{T}"/>)
/// signing path, plus the node PUBLIC key the verifier pins as the trusted issuer.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton in the composition root, built ONCE at bootstrap from the
/// resolved 32-byte root seed via <see cref="Harborline.Api.Foundation.Crypto.KeyPair.FromSeed"/>.
/// The <see cref="CurrentPrincipalSignatureRoutes"/> signing route resolves THIS so the
/// signed-principal envelope is attributed to the node's canonical identity — and so a
/// remote/TS verifier that trusts <see cref="NodePublicKey"/> accepts it.
/// </para>
/// <para>
/// <b>Key custody.</b> The secret key material lives ONLY inside the owned
/// <see cref="KeyPair"/> (NSec zeroes it on dispose). It is NEVER exported, logged, or
/// handed to the Harborline App / renderer / TS layer — the Harborline App reaches a SIGNATURE by
/// CALLING the loopback route (inc-4), it never receives the key.
/// </para>
/// </remarks>
public sealed class NodePrincipalSigner : IDisposable
{
    private readonly KeyPair _keyPair;

    /// <summary>
    /// Builds the node principal signer from the raw 32-byte root seed. The seed is
    /// consumed into the keypair and not retained as a field — only the keypair (which
    /// holds the secret as zeroed-on-dispose NSec material) lives on.
    /// </summary>
    /// <param name="rootSeed">The host's resolved 32-byte Ed25519 root seed.</param>
    /// <exception cref="ArgumentException">When <paramref name="rootSeed"/> is not 32 bytes.</exception>
    public NodePrincipalSigner(ReadOnlySpan<byte> rootSeed)
    {
        _keyPair = KeyPair.FromSeed(rootSeed);
        Signer = new Ed25519Signer(_keyPair);
        NodePublicKey = _keyPair.PrincipalId.ToBase64Url();
    }

    /// <summary>The canonical-form operation signer over the node identity.</summary>
    public IOperationSigner Signer { get; }

    /// <summary>
    /// The node PUBLIC key — base64url of the raw 32 pubkey bytes (the verifier's trust
    /// anchor; identical to <c>Signer.IssuerId.ToBase64Url()</c>).
    /// </summary>
    public string NodePublicKey { get; }

    /// <inheritdoc />
    public void Dispose() => _keyPair.Dispose();
}
