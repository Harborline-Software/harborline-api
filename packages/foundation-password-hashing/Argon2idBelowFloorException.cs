namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>
/// Thrown by <see cref="DependencyInjection.Argon2idParameterFloorAssertion"/> at host startup (and surfaced
/// by <see cref="Argon2idHashOptionsValidator"/> at options-resolution) when a
/// composition-root-configured <see cref="Argon2idHashOptions"/> parameter falls below
/// the substrate-tier floor (ADR 0097 S3 sec-eng substrate amendment). The substrate's
/// non-substitutable-downward property (§"Cryptographic floor requirements") is
/// substrate-tier-enforced rather than documentation discipline — a misconfiguration
/// such as <c>MemoryKib = 1024</c> (below the m=19456 KiB floor) fails the process at
/// startup before it serves the first request.
/// </summary>
public sealed class Argon2idBelowFloorException : Exception
{
    /// <summary>The offending <see cref="Argon2idHashOptions"/> parameter name.</summary>
    public string ParameterName { get; }

    /// <summary>The substrate-tier floor the parameter must meet or exceed.</summary>
    public long ExpectedFloor { get; }

    /// <summary>The configured value that violated the floor.</summary>
    public long ActualValue { get; }

    /// <summary>
    /// Constructs the exception naming the parameter, the expected floor, and the
    /// actual (violating) value.
    /// </summary>
    public Argon2idBelowFloorException(string parameterName, long expectedFloor, long actualValue)
        : base(BuildMessage(parameterName, expectedFloor, actualValue))
    {
        ParameterName = parameterName;
        ExpectedFloor = expectedFloor;
        ActualValue = actualValue;
    }

    private static string BuildMessage(string parameterName, long expectedFloor, long actualValue) =>
        $"Argon2idHashOptions.{parameterName} value {actualValue} is below the "
        + $"substrate-tier floor of {expectedFloor}. Substrate parameters are "
        + "non-substitutable downward per ADR 0097 §'Cryptographic floor requirements'. "
        + "Either remove the configuration override or raise the value at-or-above the "
        + "substrate floor.";
}
