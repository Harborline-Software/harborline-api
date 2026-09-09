using System.Security.Cryptography;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

// Projection-local crypto cache. Authority is still rebuilt on every fold; only an identical signed
// envelope can reuse an integrity proof. Durable edits change the key, even with an unchanged record id.
// A hydration/restart discards all proofs, including negative results for invalid signatures.
internal sealed class HydrationRosterVerifier(IOperationVerifier inner) : IOperationVerifier
{
    internal const int Capacity = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<(Type Type, string Digest, string Signature), bool> _verified = [];
    private readonly Queue<(Type Type, string Digest, string Signature)> _insertionOrder = [];

    public void Reset()
    {
        lock (_gate)
        {
            _verified.Clear();
            _insertionOrder.Clear();
        }
    }

    public bool Verify<T>(SignedOperation<T> op)
    {
        var bytes = CanonicalJson.SerializeSignable(op.Payload, op.IssuerId, op.IssuedAt, op.Nonce);
        var key = (typeof(T), Convert.ToHexString(SHA256.HashData(bytes)), op.Signature.ToBase64Url());
        lock (_gate)
        {
            if (_verified.TryGetValue(key, out var valid)) return valid;
            valid = inner.Verify(op);
            if (_verified.Count == Capacity) _verified.Remove(_insertionOrder.Dequeue());
            _verified.Add(key, valid);
            _insertionOrder.Enqueue(key);
            return valid;
        }
    }
}
