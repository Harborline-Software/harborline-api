namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>
/// Thrown by <see cref="DependencyInjection.MockPasswordHasherProductionGuardAssertion"/>
/// at host startup when an <c>ASPNETCORE_ENVIRONMENT=Production</c> composition resolves
/// an <c>IPasswordHasher&lt;TUser&gt;</c> to a concrete carrying the
/// <see cref="IMockPasswordHasher"/> marker without the explicit
/// <c>HARBORLINE_ALLOW_MOCK_PASSWORD_HASHER=true</c> opt-out (ADR 0097 D4c). Closes the
/// mock-leak-to-production foot-gun: a composition-root mis-wiring fails at startup,
/// not at the first signup request.
/// </summary>
public sealed class MockPasswordHasherInProductionException : Exception
{
    /// <summary>The registered <c>IPasswordHasher&lt;TUser&gt;</c> service (closed-generic) type.</summary>
    public Type ServiceType { get; }

    /// <summary>The offending mock concrete implementation type.</summary>
    public Type ConcreteType { get; }

    /// <summary>
    /// Constructs the exception naming the offending service + concrete types.
    /// </summary>
    public MockPasswordHasherInProductionException(Type serviceType, Type concreteType)
        : base(BuildMessage(serviceType, concreteType))
    {
        ServiceType = serviceType;
        ConcreteType = concreteType;
    }

    private static string BuildMessage(Type serviceType, Type concreteType) =>
        "Production-environment mock password hasher detected without opt-out. The "
        + $"following {serviceType} registration is a mock concrete (IMockPasswordHasher): "
        + $"{concreteType}. Either replace with a real Argon2idPasswordHasher registration "
        + "via AddHarborlinePasswordHashing<TUser>, or set "
        + "HARBORLINE_ALLOW_MOCK_PASSWORD_HASHER=true to acknowledge that this deployment "
        + "intentionally ships with the mock.";
}
