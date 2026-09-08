using Microsoft.Extensions.Options;

namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>
/// Defense-in-depth floor enforcement at the <c>IOptions&lt;T&gt;</c> validation layer
/// (ADR 0097 C3 clarification). Companion to <see cref="DependencyInjection.Argon2idParameterFloorAssertion"/>:
/// the <see cref="IValidateOptions{TOptions}"/> mechanism fires at first
/// <c>IOptions&lt;Argon2idHashOptions&gt;</c> resolution (which may be lazy); the
/// <c>IHostedService</c> assertion fires unconditionally at host startup. Both guard the
/// same non-substitutable-downward floor (§"Cryptographic floor requirements").
/// </summary>
/// <remarks>
/// Registered automatically by
/// <see cref="DependencyInjection.PasswordHashingServiceCollectionExtensions.AddHarborlinePasswordHashing{TUser}"/>
/// via <c>TryAddEnumerable</c>. Returns a single
/// <see cref="ValidateOptionsResult.Fail(System.Collections.Generic.IEnumerable{string})"/>
/// listing every below-floor parameter when any floor is violated.
/// </remarks>
internal sealed class Argon2idHashOptionsValidator : IValidateOptions<Argon2idHashOptions>
{
    public ValidateOptionsResult Validate(string? name, Argon2idHashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        Argon2idFloors.Collect(options, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
