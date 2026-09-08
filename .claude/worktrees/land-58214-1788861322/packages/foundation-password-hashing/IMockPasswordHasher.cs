namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>
/// Empty marker interface carried by <see cref="MockPasswordHasher{TUser}"/> (ADR 0097
/// D4a). The <see cref="DependencyInjection.MockPasswordHasherProductionGuardAssertion"/>
/// scans the registration tree for <c>IPasswordHasher&lt;&gt;</c> concretes carrying this
/// marker and fails the host startup when a production deployment ships the mock without
/// the explicit <c>HARBORLINE_ALLOW_MOCK_PASSWORD_HASHER=true</c> opt-out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Distinct marker family from ADR 0096's <c>IMockVendorProvider</c>.</strong>
/// Conflating the two markers would assert that the PasswordHasher is a Tier-2 vendor
/// surface — which it is not. Argon2id has one canonical algorithm (RFC 9106) and one
/// canonical .NET library (Konscious, per the OWASP cheat sheet); parameter hardening is
/// the only "swap" axis and is not vendor-shaped. ADR 0097 reuses ADR 0096's mock-first +
/// production-guard <em>discipline pattern</em> at the Tier-1 boundary, NOT ADR 0096's
/// <em>marker interface</em>.
/// </para>
/// <para>
/// <strong>Future-substrate discipline.</strong> Mock-marker interfaces are
/// substrate-scoped, not fleet-scoped. A future Tier-1 substrate that adopts the
/// mock-first discipline (e.g. a hypothetical <c>foundation-time</c> substrate with a
/// <c>MockClock</c>) introduces its own marker family (<c>IMockClock</c>) rather than
/// sharing this one — sharing markers across substrates would conflate "this substrate's
/// mock" semantics. The discipline IS the pattern; the marker is the substrate's instance
/// of the pattern (ADR 0097 §"Cross-tier mock-marker family discipline").
/// </para>
/// </remarks>
public interface IMockPasswordHasher
{
}
