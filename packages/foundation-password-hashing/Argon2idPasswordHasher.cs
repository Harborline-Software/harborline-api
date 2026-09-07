using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>
/// Tier-1 substrate <see cref="IPasswordHasher{TUser}"/> implemented over the Argon2id
/// primitive (Konscious; ADR 0097 Option C). Writes the Konscious-canonical Argon2id PHC
/// string going forward; verifies BOTH the PHC string format and the legacy ASP.NET
/// Identity V3 PBKDF2 <c>byte[]</c> format. Returns
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> on legacy-V3 verify
/// success (algorithm upgrade) and on below-floor-parameter PHC verify success (parameter
/// upgrade) — the unified migration trigger the W#80 login handler observes to rehash and
/// persist the canonical format (Q4 RESOLVED — unified trigger; no
/// <c>PasswordVerificationResultExtended</c>).
/// </summary>
/// <remarks>
/// <para>
/// Parameters are captured from an <c>IOptions&lt;Argon2idHashOptions&gt;</c> snapshot at
/// construction (D7 / Halt 11 immutability). The PHC wire format is self-describing
/// (<c>$argon2id$v=19$m={m},t={t},p={p}$&lt;b64-salt&gt;$&lt;b64-hash&gt;</c>) so the
/// verify path inspects embedded parameters with no auxiliary metadata storage.
/// </para>
/// <para>
/// <strong>No-log discipline (Floor 7).</strong> This type NEVER logs the plaintext
/// password, the salt, the hash, or the pepper. Verification failures return
/// <see cref="PasswordVerificationResult.Failed"/> rather than throwing — a corrupt or
/// unrecognized stored hash is an authentication failure, not a substrate error (matches
/// BCL <see cref="PasswordHasher{TUser}"/> behavior).
/// </para>
/// </remarks>
/// <typeparam name="TUser">The user entity type (TUser-agnostic — the hash does not read it).</typeparam>
public sealed class Argon2idPasswordHasher<TUser> : IPasswordHasher<TUser>
    where TUser : class
{
    // S6 sec-eng substrate amendment — input-length defense-in-depth floors.
    private const int MaxHashedPasswordLength = 1024;   // PHC strings are < 200 chars; generous ceiling.
    private const int MaxProvidedPasswordLength = 4096;  // 4 KiB — above any plausible passphrase.

    private const string PhcPrefix = "$argon2id$";
    private const int Argon2Version = 19; // 0x13 — Argon2 v1.3 (RFC 9106).

    private readonly Argon2idHashOptions _options;

    // Legacy ASP.NET Identity V3 PBKDF2 fallback verifier (Halt 10). Composed privately;
    // PasswordHasherCompatibilityMode.IdentityV3 = PBKDF2-HMAC-SHA256, 100k iter, version byte 0x01.
    private readonly PasswordHasher<TUser> _legacyHasher = new(
        Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        }));

    /// <summary>
    /// Constructs the hasher, capturing the bound <see cref="Argon2idHashOptions"/> snapshot.
    /// </summary>
    public Argon2idPasswordHasher(IOptions<Argon2idHashOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public string HashPassword(TUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        // S6 — substrate-tier complement to the W#79 handler-tier input-length cap.
        if (password.Length > MaxProvidedPasswordLength)
        {
            throw new ArgumentException(
                $"Provided password length {password.Length} exceeds the substrate-tier "
                + $"maximum of {MaxProvidedPasswordLength} characters.",
                nameof(password));
        }

        var salt = new byte[_options.SaltLengthBytes];
        RandomNumberGenerator.Fill(salt); // BCL CSPRNG; Span-typed, allocation-free (C8).

        var hash = ComputeArgon2id(
            Encoding.UTF8.GetBytes(password), // Floor 6 — no truncation, no pre-digest.
            salt,
            _options.MemoryKib,
            _options.Iterations,
            _options.DegreeOfParallelism,
            _options.HashLengthBytes,
            _options.Pepper);

        return FormatPhcString(
            _options.MemoryKib,
            _options.Iterations,
            _options.DegreeOfParallelism,
            salt,
            hash);
    }

    /// <inheritdoc />
    public PasswordVerificationResult VerifyHashedPassword(
        TUser user,
        string hashedPassword,
        string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(hashedPassword);
        ArgumentNullException.ThrowIfNull(providedPassword);

        // S6 — input-length floors short-circuit-Failed before any parsing/hashing.
        if (hashedPassword.Length > MaxHashedPasswordLength)
        {
            return PasswordVerificationResult.Failed;
        }

        if (providedPassword.Length > MaxProvidedPasswordLength)
        {
            return PasswordVerificationResult.Failed;
        }

        ReadOnlySpan<char> stored = hashedPassword.AsSpan();

        // PHC string — the canonical Argon2id format this substrate writes.
        if (stored.StartsWith(PhcPrefix, StringComparison.Ordinal))
        {
            return VerifyPhcString(stored, providedPassword);
        }

        // Legacy ASP.NET Identity V3 byte[] — Base64 decodes to a leading 0x01 version byte.
        if (IsLegacyV3Format(hashedPassword))
        {
            var legacyResult = _legacyHasher.VerifyHashedPassword(user, hashedPassword, providedPassword);
            return legacyResult == PasswordVerificationResult.Success
                ? PasswordVerificationResult.SuccessRehashNeeded // algorithm upgrade trigger
                : PasswordVerificationResult.Failed;
        }

        // Unrecognized / corrupt — authentication failure, not a substrate error.
        return PasswordVerificationResult.Failed;
    }

    private PasswordVerificationResult VerifyPhcString(ReadOnlySpan<char> stored, string providedPassword)
    {
        if (!TryParsePhcString(stored, out var parts))
        {
            return PasswordVerificationResult.Failed;
        }

        byte[] recomputed;
        try
        {
            recomputed = ComputeArgon2id(
                Encoding.UTF8.GetBytes(providedPassword),
                parts.Salt,
                parts.MemoryKib,
                parts.Iterations,
                parts.DegreeOfParallelism,
                (uint)parts.Hash.Length,
                _options.Pepper);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Malformed parameter values that survived parsing but the primitive rejects.
            return PasswordVerificationResult.Failed;
        }

        // Constant-time comparison via the BCL primitive (C6) — never a naive ==.
        if (!CryptographicOperations.FixedTimeEquals(recomputed, parts.Hash))
        {
            return PasswordVerificationResult.Failed;
        }

        // Below-floor-parameter success → parameter upgrade trigger.
        var belowFloor =
            parts.MemoryKib < _options.MemoryKib
            || parts.Iterations < _options.Iterations
            || parts.DegreeOfParallelism < _options.DegreeOfParallelism;

        return belowFloor
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }

    /// <summary>
    /// Computes an Argon2id hash via Konscious. The <c>Task.Run(...).GetAwaiter().GetResult()</c>
    /// hop (A5 LOAD-BEARING; Option B) forces the awaited continuation to escape any caller's
    /// <see cref="SynchronizationContext"/> by hopping to a pool thread first, eliminating the
    /// Blazor-Server SynchronizationContext deadlock-class hazard regardless of Konscious's
    /// internal <c>ConfigureAwait</c> choice. Cost is one additional thread-pool hop per call,
    /// negligible against the ~50-100 ms Argon2id wall-clock.
    /// </summary>
    private static byte[] ComputeArgon2id(
        byte[] passwordBytes,
        byte[] salt,
        uint memoryKib,
        uint iterations,
        uint degreeOfParallelism,
        uint hashLengthBytes,
        byte[]? pepper)
    {
        using var argon2id = new Argon2id(passwordBytes)
        {
            Salt = salt,
            Iterations = (int)iterations,
            MemorySize = (int)memoryKib, // C7 — Konscious MemorySize is int; cast at the assignment site.
            DegreeOfParallelism = (int)degreeOfParallelism,
        };

        if (pepper is not null)
        {
            // S1 — pepper via the Argon2 KnownSecret surface (RFC 9106 §3.1 K parameter).
            // NEVER XOR-ed into the password bytes.
            argon2id.KnownSecret = pepper;
        }

        return Task.Run(() => argon2id.GetBytesAsync((int)hashLengthBytes)).GetAwaiter().GetResult();
    }

    private static string FormatPhcString(
        uint memoryKib,
        uint iterations,
        uint degreeOfParallelism,
        byte[] salt,
        byte[] hash)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{PhcPrefix}v={Argon2Version}$m={memoryKib},t={iterations},p={degreeOfParallelism}"
            + $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>
    /// A legacy ASP.NET Identity V3 hash is a Base64-encoded blob whose decoded first byte
    /// is <c>0x01</c> (the V3 format marker). Returns false for any non-base64 input.
    /// </summary>
    private static bool IsLegacyV3Format(string hashedPassword)
    {
        // Generous upper bound — a V3 blob (marker + prf + iter + saltlen + salt + subkey)
        // is well under 256 bytes; allocate accordingly.
        Span<byte> decoded = stackalloc byte[256];
        if (!Convert.TryFromBase64String(hashedPassword, decoded, out var written) || written == 0)
        {
            return false;
        }

        return decoded[0] == 0x01;
    }

    /// <summary>
    /// Parses a Konscious-canonical Argon2id PHC string into its parameter + salt + hash
    /// parts. Returns false (no throw) on any structural / encoding defect — the verify
    /// path treats a malformed stored hash as an authentication failure.
    /// Format: <c>$argon2id$v=19$m={m},t={t},p={p}$&lt;b64-salt&gt;$&lt;b64-hash&gt;</c>.
    /// </summary>
    internal static bool TryParsePhcString(ReadOnlySpan<char> stored, out Argon2idPhcParts parts)
    {
        parts = default;

        // Sections split on '$'. Leading '$' yields an empty [0]; expect 6 sections:
        // ["", "argon2id", "v=19", "m=..,t=..,p=..", "<b64-salt>", "<b64-hash>"].
        Span<Range> ranges = stackalloc Range[8];
        var count = stored.Split(ranges, '$');
        if (count != 6)
        {
            return false;
        }

        if (!stored[ranges[1]].SequenceEqual("argon2id"))
        {
            return false;
        }

        // v=NN — version segment; require the value parse and equal the Argon2 v1.3 marker.
        var versionSeg = stored[ranges[2]];
        if (!versionSeg.StartsWith("v=", StringComparison.Ordinal)
            || !int.TryParse(versionSeg[2..], out var version)
            || version != Argon2Version)
        {
            return false;
        }

        if (!TryParseParameters(stored[ranges[3]], out var m, out var t, out var p))
        {
            return false;
        }

        if (!TryDecodeBase64(stored[ranges[4]], out var salt)
            || !TryDecodeBase64(stored[ranges[5]], out var hash)
            || salt.Length == 0
            || hash.Length == 0)
        {
            return false;
        }

        parts = new Argon2idPhcParts(m, t, p, salt, hash);
        return true;
    }

    private static bool TryParseParameters(
        ReadOnlySpan<char> segment,
        out uint memoryKib,
        out uint iterations,
        out uint degreeOfParallelism)
    {
        memoryKib = 0;
        iterations = 0;
        degreeOfParallelism = 0;

        // "m={m},t={t},p={p}" — exactly three comma-separated key=value pairs, in order.
        Span<Range> kv = stackalloc Range[4];
        var count = segment.Split(kv, ',');
        if (count != 3)
        {
            return false;
        }

        return TryParseUintPair(segment[kv[0]], "m=", out memoryKib)
            && TryParseUintPair(segment[kv[1]], "t=", out iterations)
            && TryParseUintPair(segment[kv[2]], "p=", out degreeOfParallelism);
    }

    private static bool TryParseUintPair(ReadOnlySpan<char> pair, string prefix, out uint value)
    {
        value = 0;
        return pair.StartsWith(prefix, StringComparison.Ordinal)
            && uint.TryParse(pair[prefix.Length..], out value);
    }

    private static bool TryDecodeBase64(ReadOnlySpan<char> segment, out byte[] decoded)
    {
        // Max PHC salt/hash sections are small; 1 KiB ceiling well covers them.
        Span<byte> buffer = stackalloc byte[1024];
        if (Convert.TryFromBase64Chars(segment, buffer, out var written))
        {
            decoded = buffer[..written].ToArray();
            return true;
        }

        decoded = [];
        return false;
    }
}

/// <summary>
/// Parsed parts of a Konscious-canonical Argon2id PHC string (parameters + salt + hash).
/// </summary>
internal readonly record struct Argon2idPhcParts(
    uint MemoryKib,
    uint Iterations,
    uint DegreeOfParallelism,
    byte[] Salt,
    byte[] Hash);
