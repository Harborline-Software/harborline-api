namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>
/// Shared substrate-tier floor definitions for <see cref="Argon2idHashOptions"/>
/// (ADR 0097 §"Cryptographic floor requirements"). Single source of truth consumed by
/// both <see cref="DependencyInjection.Argon2idParameterFloorAssertion"/> (host-startup throw on first
/// violation) and <see cref="Argon2idHashOptionsValidator"/> (options-resolution collect
/// all violations). Each floor is non-substitutable downward — implementations MAY
/// tighten (higher) but MUST NOT loosen (lower).
/// </summary>
internal static class Argon2idFloors
{
    public const uint MemoryKibFloor = 19456;   // Floor 3 — OWASP minimum (19 MiB).
    public const uint IterationsFloor = 2;      // Floor 4 — OWASP minimum at m=19 MiB.
    public const uint DegreeOfParallelismFloor = 1; // Floor 5.
    public const uint SaltLengthBytesFloor = 16; // Floor 1.
    public const uint HashLengthBytesFloor = 32; // Floor 2.
    public const int PepperLengthCeiling = 64;   // Floor 6 — future-enablement bound (≤ 64 bytes).

    /// <summary>
    /// Returns the first below-floor violation as a tuple, or <c>null</c> when all floors
    /// hold. Used by <see cref="DependencyInjection.Argon2idParameterFloorAssertion"/> (throw-on-first).
    /// </summary>
    public static (string ParameterName, long ExpectedFloor, long ActualValue)? FirstViolation(
        Argon2idHashOptions options)
    {
        if (options.MemoryKib < MemoryKibFloor)
        {
            return (nameof(Argon2idHashOptions.MemoryKib), MemoryKibFloor, options.MemoryKib);
        }

        if (options.Iterations < IterationsFloor)
        {
            return (nameof(Argon2idHashOptions.Iterations), IterationsFloor, options.Iterations);
        }

        if (options.DegreeOfParallelism < DegreeOfParallelismFloor)
        {
            return (nameof(Argon2idHashOptions.DegreeOfParallelism), DegreeOfParallelismFloor, options.DegreeOfParallelism);
        }

        if (options.SaltLengthBytes < SaltLengthBytesFloor)
        {
            return (nameof(Argon2idHashOptions.SaltLengthBytes), SaltLengthBytesFloor, options.SaltLengthBytes);
        }

        if (options.HashLengthBytes < HashLengthBytesFloor)
        {
            return (nameof(Argon2idHashOptions.HashLengthBytes), HashLengthBytesFloor, options.HashLengthBytes);
        }

        if (options.Pepper is not null && options.Pepper.Length > PepperLengthCeiling)
        {
            return (nameof(Argon2idHashOptions.Pepper) + ".Length", PepperLengthCeiling, options.Pepper.Length);
        }

        return null;
    }

    /// <summary>
    /// Appends a descriptive error message for every below-floor parameter to
    /// <paramref name="failures"/>. Used by <see cref="Argon2idHashOptionsValidator"/>
    /// (collect-all). A no-op when all floors hold.
    /// </summary>
    public static void Collect(Argon2idHashOptions options, List<string> failures)
    {
        if (options.MemoryKib < MemoryKibFloor)
        {
            failures.Add(Message(nameof(Argon2idHashOptions.MemoryKib), MemoryKibFloor, options.MemoryKib));
        }

        if (options.Iterations < IterationsFloor)
        {
            failures.Add(Message(nameof(Argon2idHashOptions.Iterations), IterationsFloor, options.Iterations));
        }

        if (options.DegreeOfParallelism < DegreeOfParallelismFloor)
        {
            failures.Add(Message(nameof(Argon2idHashOptions.DegreeOfParallelism), DegreeOfParallelismFloor, options.DegreeOfParallelism));
        }

        if (options.SaltLengthBytes < SaltLengthBytesFloor)
        {
            failures.Add(Message(nameof(Argon2idHashOptions.SaltLengthBytes), SaltLengthBytesFloor, options.SaltLengthBytes));
        }

        if (options.HashLengthBytes < HashLengthBytesFloor)
        {
            failures.Add(Message(nameof(Argon2idHashOptions.HashLengthBytes), HashLengthBytesFloor, options.HashLengthBytes));
        }

        if (options.Pepper is not null && options.Pepper.Length > PepperLengthCeiling)
        {
            failures.Add(
                $"Argon2idHashOptions.Pepper.Length value {options.Pepper.Length} exceeds the "
                + $"substrate-tier ceiling of {PepperLengthCeiling} bytes.");
        }
    }

    private static string Message(string parameterName, long expectedFloor, long actualValue) =>
        $"Argon2idHashOptions.{parameterName} value {actualValue} is below the "
        + $"substrate-tier floor of {expectedFloor}.";
}
