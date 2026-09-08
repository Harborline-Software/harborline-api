namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>
/// Substrate-tier Argon2id parameter options for <see cref="Argon2idPasswordHasher{TUser}"/>
/// (ADR 0097). Bound via <c>services.Configure&lt;Argon2idHashOptions&gt;(...)</c> or the
/// <see cref="DependencyInjection.PasswordHashingServiceCollectionExtensions.AddHarborlinePasswordHashing{TUser}"/>
/// optional <c>configure</c> delegate. Captured as an <c>IOptions&lt;T&gt;</c> singleton
/// snapshot at composition-root build time and immutable for the process lifetime
/// (ADR 0097 D7 / Halt 11 — substrate-tier parameter changes are deployment-tier events,
/// not runtime-reload events).
/// </summary>
/// <remarks>
/// <para>
/// Defaults are pinned at the OWASP password-storage cheat-sheet minimum (retrieved
/// 2026-05-27; cross-references RFC 9106 §4): <c>m = 19456 KiB (19 MiB)</c>,
/// <c>t = 2</c>, <c>p = 1</c>. ~50-100 ms wall-clock on modern x86. Deployments MAY
/// raise the parameters (more memory / more iterations) but MUST NOT lower them below
/// the substrate floors — the <see cref="DependencyInjection.Argon2idParameterFloorAssertion"/>
/// (host-startup) and <see cref="Argon2idHashOptionsValidator"/> (options-resolution)
/// enforce the non-substitutable-downward property by construction (ADR 0097
/// §"Cryptographic floor requirements").
/// </para>
/// <para>
/// <see cref="Pepper"/> is <c>null</c> at MVP (Halt 7) — pepper is OWASP-optional
/// defense-in-depth that requires a secret-store substrate (rotation + audit +
/// compromise semantics) deferred to a future ADR. When set, it is applied via the
/// Argon2 <c>KnownSecret</c> property (RFC 9106 §3.1 <c>K</c> parameter; S1 sec-eng
/// substrate amendment) — NEVER XOR-ed into the password bytes.
/// </para>
/// <para>
/// Properties carry public setters so both the <c>IConfiguration</c> binder AND the
/// imperative <c>Action&lt;Argon2idHashOptions&gt;</c> <c>configure</c> delegate of
/// <see cref="DependencyInjection.PasswordHashingServiceCollectionExtensions.AddHarborlinePasswordHashing{TUser}"/>
/// (C1 / A6) can set them — the canonical ASP.NET Core options shape. The ADR C2
/// clarification proposed <c>init</c>-only setters, but <c>init</c> is incompatible with
/// the imperative configure delegate (an <c>init</c> accessor cannot be assigned outside an
/// object initializer / constructor), so the options carry <c>set</c> accessors. Effective
/// immutability is preserved by the <c>IOptions&lt;T&gt;</c> singleton-snapshot capture in
/// <see cref="Argon2idPasswordHasher{TUser}"/>'s constructor (D7 / Halt 11). Deviation
/// flagged in the Step 1 PR.
/// </para>
/// </remarks>
public sealed class Argon2idHashOptions
{
    /// <summary>Memory cost in KiB. Default 19456 (= 19 MiB; OWASP minimum; Floor 3).</summary>
    public uint MemoryKib { get; set; } = 19456;

    /// <summary>Iteration (time) cost. Default 2 (OWASP minimum at the m=19 MiB tier; Floor 4).</summary>
    public uint Iterations { get; set; } = 2;

    /// <summary>Degree of parallelism. Default 1 (OWASP minimum; Floor 5).</summary>
    public uint DegreeOfParallelism { get; set; } = 1;

    /// <summary>Salt length in bytes. Default 16 (Floor 1). Generated per-hash via the BCL
    /// <see cref="System.Security.Cryptography.RandomNumberGenerator"/>.</summary>
    public uint SaltLengthBytes { get; set; } = 16;

    /// <summary>Hash output length in bytes. Default 32 (Floor 2; matches Argon2id-1.3 default).</summary>
    public uint HashLengthBytes { get; set; } = 32;

    /// <summary>
    /// Optional pepper (server-side secret) applied via the Argon2 <c>KnownSecret</c>
    /// surface (RFC 9106 §3.1 <c>K</c>). <c>null</c> at MVP (Halt 7). When set, the length
    /// MUST be ≤ 64 bytes (Floor 6 future-enablement bound; enforced at startup by
    /// <see cref="DependencyInjection.Argon2idParameterFloorAssertion"/>).
    /// </summary>
    public byte[]? Pepper { get; set; }
}
