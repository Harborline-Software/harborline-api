using System.Security.Cryptography;
using System.Text;

namespace Harborline.Api.Kernel.Security.Keys;

/// <summary>
/// HKDF-SHA256 safety-code derivation from an authenticated shared secret, per ADR 0152.
/// </summary>
/// <remarks>
/// Shape mirrors <see cref="HkdfX25519SubkeyDerivation"/>: UTF-8 domain separation, BCL HKDF,
/// and explicit zeroing of the secret-derived intermediate. The HKDF output includes an eight-byte
/// modulo-bias margin before <see cref="SasEncode"/> maps it to decimal.
/// </remarks>
public sealed class SafetyCodeDerivation : ISafetyCodeDerivation
{
    /// <summary>Frozen crypto identifier for the v1 safety-code domain.</summary>
    public const string InfoPrefix = "sunfish-safety-code-v1:";

    /// <summary>Minimum authenticated shared-secret length: 256 bits.</summary>
    public const int MinimumSharedSecretLength = 32;

    private const int ModuloBiasMarginBytes = 8;

    /// <inheritdoc />
    public string Derive(
        ReadOnlyMemory<byte> sharedSecret,
        string engagementId,
        string label,
        int digits = SasEncode.DefaultDigits)
    {
        if (sharedSecret.Length < MinimumSharedSecretLength)
        {
            throw new ArgumentException(
                "Authenticated shared secret must contain at least 32 bytes.",
                nameof(sharedSecret));
        }

        ArgumentException.ThrowIfNullOrEmpty(engagementId);
        ArgumentException.ThrowIfNullOrEmpty(label);
        SasEncode.ValidateDigitCount(digits);

        var salt = Encoding.UTF8.GetBytes(engagementId);
        var prefix = Encoding.UTF8.GetBytes(InfoPrefix);
        var labelBytes = Encoding.UTF8.GetBytes(label);
        var info = new byte[prefix.Length + labelBytes.Length];
        Buffer.BlockCopy(prefix, 0, info, 0, prefix.Length);
        Buffer.BlockCopy(labelBytes, 0, info, prefix.Length, labelBytes.Length);

        var outputLength = GetOutputLength(digits);
        var output = new byte[outputLength];

        try
        {
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: sharedSecret.Span,
                output: output,
                salt: salt,
                info: info);
            return SasEncode.Encode(output, digits);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    internal static int GetOutputLength(int digits)
    {
        SasEncode.ValidateDigitCount(digits);
        return checked(
            (int)Math.Ceiling(digits * Math.Log2(10d) / 8d) + ModuloBiasMarginBytes);
    }
}
