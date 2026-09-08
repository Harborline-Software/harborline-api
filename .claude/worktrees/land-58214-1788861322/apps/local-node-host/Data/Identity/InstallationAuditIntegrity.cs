using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Canonical hashing rules shared by every installation-identity audit writer.</summary>
internal static class InstallationAuditIntegrity
{
    internal const string ZeroHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    internal static string ComputeEnvelopeHash(InstallationAuditEnvelopeRecord envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Hash(
            envelope.InstallationIdentityId,
            envelope.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.CorrelationId,
            envelope.CommandFingerprint,
            envelope.EventType,
            envelope.ActorKind,
            envelope.ActorId,
            envelope.RootEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.RootPublicKeyFingerprint,
            envelope.PreviousHash,
            envelope.PayloadDigest,
            envelope.OccurredAtUtc.ToUnixTimeMilliseconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static bool HasValidEnvelopeHash(InstallationAuditEnvelopeRecord envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(envelope.EnvelopeHash),
            Encoding.UTF8.GetBytes(ComputeEnvelopeHash(envelope)));
    }

    internal static bool HasValidChain(
        IReadOnlyList<InstallationAuditEnvelopeRecord> chain,
        InstallationAuditHeadRecord head,
        string installationIdentityId)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationIdentityId);
        if (chain.Count != head.Sequence)
        {
            return false;
        }

        var previousHash = ZeroHash;
        for (var index = 0; index < chain.Count; index++)
        {
            var envelope = chain[index];
            if (envelope.Sequence != index + 1L ||
                !string.Equals(envelope.InstallationIdentityId, installationIdentityId,
                    StringComparison.Ordinal) ||
                !string.Equals(envelope.PreviousHash, previousHash, StringComparison.Ordinal) ||
                !HasValidEnvelopeHash(envelope))
            {
                return false;
            }
            previousHash = envelope.EnvelopeHash;
        }

        return chain.Count != 0 &&
            string.Equals(previousHash, head.HeadHash, StringComparison.Ordinal);
    }

    internal static string Hash(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
