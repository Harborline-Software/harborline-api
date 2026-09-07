using System.Security.Cryptography;

namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>Canonical inspection gate for persisted Argon2id credential artifacts.</summary>
public static class Argon2idCredentialArtifact
{
    // ADR 0097 D6 names 128 MiB / t=3 / p=1 as the highest reviewed defensive profile.
    // Salt and output remain bounded to twice the canonical defaults to prevent hostile artifacts
    // from turning verification into unbounded allocation or CPU work.
    private const uint MemoryKibCeiling = 131072;
    private const uint IterationsCeiling = 3;
    private const uint DegreeOfParallelismCeiling = 1;
    private const int SaltLengthBytesCeiling = 64;
    private const int HashLengthBytesCeiling = 64;

    /// <summary>The stable storage identifier for the canonical Argon2id v1.3 PHC format.</summary>
    public const string AlgorithmId = "argon2id-phc/v19";

    /// <summary>
    /// Returns true only for canonical PHC artifacts within the reviewed ADR 0097 verification policy.
    /// Decoded hash material is zeroed before the method returns.
    /// </summary>
    public static bool IsCanonicalAndWithinVerificationPolicy(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded) ||
            !Argon2idPasswordHasher<object>.TryParsePhcString(encoded.AsSpan(), out var parts))
        {
            return false;
        }

        try
        {
            var canonical = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"$argon2id$v=19$m={parts.MemoryKib},t={parts.Iterations},p={parts.DegreeOfParallelism}" +
                $"${Convert.ToBase64String(parts.Salt)}${Convert.ToBase64String(parts.Hash)}");
            return string.Equals(encoded, canonical, StringComparison.Ordinal) &&
                parts.MemoryKib >= Argon2idFloors.MemoryKibFloor &&
                parts.MemoryKib <= MemoryKibCeiling &&
                parts.Iterations >= Argon2idFloors.IterationsFloor &&
                parts.Iterations <= IterationsCeiling &&
                parts.DegreeOfParallelism >= Argon2idFloors.DegreeOfParallelismFloor &&
                parts.DegreeOfParallelism <= DegreeOfParallelismCeiling &&
                parts.Salt.Length >= Argon2idFloors.SaltLengthBytesFloor &&
                parts.Salt.Length <= SaltLengthBytesCeiling &&
                parts.Hash.Length >= Argon2idFloors.HashLengthBytesFloor &&
                parts.Hash.Length <= HashLengthBytesCeiling;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parts.Salt);
            CryptographicOperations.ZeroMemory(parts.Hash);
        }
    }
}
