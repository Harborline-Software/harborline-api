using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// An installation authority version. The durable marker records this; the stage machine moves
/// between versions; a <see cref="IInstallationAuthorityVersionPolicy"/> owns what each one means.
/// </summary>
public enum InstallationAuthorityVersion
{
    /// <summary>The incumbent. Every web identity path in this installation is currently V1.</summary>
    V1 = 1,

    /// <summary>The successor described by ADR 0160 Revision 3.</summary>
    V2 = 2,
}

/// <summary>What a caller is asking to be admitted to do.</summary>
public enum InstallationCutoverAdmissionKind
{
    /// <summary>A mutation of v1-authoritative identity state.</summary>
    V1Mutation,

    /// <summary>Acceptance of a legacy bearer credential. The subject is the audience.</summary>
    LegacyBearer,
}

/// <summary>
/// A marker implemented by a REGISTERED v2 sign-in path — an account-challenge issuer and tenant
/// selector that can serve login once the authority-version marker commits.
/// </summary>
/// <remarks>
/// <b>Nothing implements this today, and that is the entire point.</b>
///
/// ADR 0160 R3-H makes the installation reject every v1 cookie audience permanently once the marker
/// commits. Before this interface existed the only precondition on that commit was that the v2 DATA
/// substrate was in place — root designation, identity row, account, grant. Data readiness says
/// nothing about whether anyone can still sign in, and the only registered
/// <c>IWebAccountAccessChallengeIssuer</c> and <c>IWebTenantSelectionAuthority</c> are the v1
/// implementations. A flip satisfying every check would have left the installation unable to
/// authenticate anybody, permanently, with no rollback (rollback after the marker is capability
/// disable, not a return to v1).
///
/// That could not be checked from the orchestrator, which holds a <c>DbContext</c> and cannot see
/// what the composition root registered. A POLICY can, because a policy is itself a registered
/// object and takes its collaborators by injection. <see cref="V2InstallationAuthorityVersionPolicy"/>
/// requires at least one implementation of this interface before it reports ready, so the flip is
/// refused until a successor genuinely exists.
///
/// When the v2 issuer and selector are built, they implement this. Until then the absence is not an
/// oversight — it is the accurate answer to "can this installation serve v2 sign-in".
/// </remarks>
public interface IInstallationAuthorityV2SignInPath
{
    /// <summary>Diagnostic name, surfaced when explaining why a cutover was or was not permitted.</summary>
    string SignInPathName { get; }
}

/// <summary>
/// Version-specific readiness, admission and retirement rules. One implementation per authority
/// version; the cutover orchestrator owns the durable stage machine and consults these.
/// </summary>
/// <remarks>
/// The split is deliberate and is the rule to follow when adding V3 and beyond. A new version that
/// only changes RULES adds a policy. A new version that changes the TRANSITION SHAPE adds a stage
/// and a policy together. Callers never name a version — they consult the gate, which resolves the
/// current version from durable state and asks its policy.
/// </remarks>
public interface IInstallationAuthorityVersionPolicy
{
    /// <summary>The version this policy speaks for.</summary>
    InstallationAuthorityVersion Version { get; }

    /// <summary>
    /// Whether this version is ready to BECOME authoritative. Consulted before the marker commits.
    /// A policy that cannot establish readiness must answer false: the cost of a wrong <c>true</c>
    /// is an installation that cannot sign anyone in, and the cost of a wrong <c>false</c> is a
    /// refused cutover that can be retried.
    /// </summary>
    Task<bool> IsReadyAsync(
        NodeLocalInstallationIdentityDbContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether a request is admitted while THIS version is authoritative. <paramref name="subject"/>
    /// carries the kind-specific detail — for <see cref="InstallationCutoverAdmissionKind.LegacyBearer"/>
    /// it is the <see cref="InstallationIdentityLegacyBearerAudience"/>. The durable stage and
    /// barrier values are supplied because each version owns the conditions under which its
    /// admission rule applies.
    /// </summary>
    Task<bool> IsAdmissionAllowedAsync(
        InstallationIdentityCutoverStage stage,
        long v1WriteBarrierVersion,
        InstallationCutoverAdmissionKind kind,
        object? subject,
        CancellationToken cancellationToken);

    /// <summary>The refusal code returned when this version refuses an admission as retired.</summary>
    string? RetirementRefusalCode { get; }

    /// <summary>
    /// The bearer audiences this version retires, in a stable order. Replaces enumerating the
    /// audience enum wholesale, which baked a v1-shaped assumption into the stage machine — a v3
    /// that retires a different set would have been silently wrong.
    /// </summary>
    IReadOnlyList<InstallationIdentityLegacyBearerAudience> RetiredAudiences { get; }
}

/// <summary>Resolves the policy for an authority version.</summary>
public interface IInstallationAuthorityVersionRegistry
{
    /// <summary>
    /// The policy for <paramref name="version"/>. Throws when no policy is registered: an unknown
    /// version must not silently resolve to a permissive default.
    /// </summary>
    IInstallationAuthorityVersionPolicy Get(InstallationAuthorityVersion version);
}

/// <summary>
/// V1 — the incumbent. While v1 is authoritative its own paths work, which is what "authoritative"
/// means. Its admission rule owns the exact durable stage and barrier conditions for that answer.
/// </summary>
public sealed class V1InstallationAuthorityVersionPolicy : IInstallationAuthorityVersionPolicy
{
    public InstallationAuthorityVersion Version => InstallationAuthorityVersion.V1;

    public string? RetirementRefusalCode => null;

    /// <summary>The incumbent retires no audience while it remains authoritative.</summary>
    public IReadOnlyList<InstallationIdentityLegacyBearerAudience> RetiredAudiences { get; } =
        Array.Empty<InstallationIdentityLegacyBearerAudience>();

    /// <summary>
    /// Always ready. You never advance TO v1 — it is where the installation starts, and a rollback
    /// before the marker leaves it authoritative rather than transitioning into it.
    /// </summary>
    public Task<bool> IsReadyAsync(
        NodeLocalInstallationIdentityDbContext context,
        CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> IsAdmissionAllowedAsync(
        InstallationIdentityCutoverStage stage,
        long v1WriteBarrierVersion,
        InstallationCutoverAdmissionKind kind,
        object? subject,
        CancellationToken cancellationToken)
        => Task.FromResult(
            stage == InstallationIdentityCutoverStage.LegacyV1Authoritative &&
            v1WriteBarrierVersion == 0);
}

/// <summary>
/// V2 — the successor. Ready only when its data substrate exists AND a registered sign-in path can
/// serve login afterwards. Refuses every v1 admission once authoritative, per ADR 0160 R3-H.
/// </summary>
public sealed class V2InstallationAuthorityVersionPolicy : IInstallationAuthorityVersionPolicy
{
    private readonly IReadOnlyList<IInstallationAuthorityV2SignInPath> _signInPaths;

    public V2InstallationAuthorityVersionPolicy(
        IEnumerable<IInstallationAuthorityV2SignInPath> signInPaths)
    {
        ArgumentNullException.ThrowIfNull(signInPaths);
        _signInPaths = signInPaths.ToArray();
    }

    public InstallationAuthorityVersion Version => InstallationAuthorityVersion.V2;

    public string? RetirementRefusalCode =>
        InstallationIdentityCutoverOrchestrator.LegacyAuthorityRetiredRefusal;

    public IReadOnlyList<InstallationIdentityLegacyBearerAudience> RetiredAudiences { get; } =
        Enum.GetValues<InstallationIdentityLegacyBearerAudience>().Order().ToArray();

    /// <summary>The registered successor sign-in paths, for diagnostics.</summary>
    public IReadOnlyList<string> SignInPathNames =>
        _signInPaths.Select(path => path.SignInPathName).ToArray();

    /// <summary>
    /// BOTH halves are required, and the second is the one that did not exist before. Data readiness
    /// is the same substrate check the orchestrator used to make inline. The sign-in-path check is
    /// what stops a flip that would leave nobody able to authenticate.
    /// </summary>
    public async Task<bool> IsReadyAsync(
        NodeLocalInstallationIdentityDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_signInPaths.Count == 0)
        {
            return false;
        }

        return await InstallationIdentityCutoverOrchestrator
            .HasReadableV2CandidateAsync(context, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Nothing v1 is admitted once v2 is authoritative. This is the retirement ADR 0160 R3-H
    /// specifies: the listener rejects every v1 cookie or invitation audience after the marker flip
    /// even if a legacy source row remains physically present.
    /// </summary>
    public Task<bool> IsAdmissionAllowedAsync(
        InstallationIdentityCutoverStage stage,
        long v1WriteBarrierVersion,
        InstallationCutoverAdmissionKind kind,
        object? subject,
        CancellationToken cancellationToken)
        => Task.FromResult(false);
}

/// <summary>Fixed registry over the policies supplied at construction.</summary>
public sealed class InstallationAuthorityVersionRegistry : IInstallationAuthorityVersionRegistry
{
    private readonly IReadOnlyDictionary<InstallationAuthorityVersion, IInstallationAuthorityVersionPolicy> _policies;

    public InstallationAuthorityVersionRegistry(
        IEnumerable<IInstallationAuthorityVersionPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var byVersion = new Dictionary<InstallationAuthorityVersion, IInstallationAuthorityVersionPolicy>();
        foreach (var policy in policies)
        {
            // Refuse duplicates rather than letting registration order decide which rules apply.
            // Two policies for one version is a composition error, and picking one silently would
            // make the effective rule set depend on DI ordering.
            if (!byVersion.TryAdd(policy.Version, policy))
            {
                throw new InvalidOperationException(
                    $"Two policies registered for authority version {policy.Version}.");
            }
        }

        _policies = byVersion;
    }

    public IInstallationAuthorityVersionPolicy Get(InstallationAuthorityVersion version) =>
        _policies.TryGetValue(version, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No authority-version policy is registered for {version}.");

    /// <summary>
    /// The composition every deployed profile uses, and the fail-closed default for callers that do
    /// not supply one. V2 gets no sign-in paths here, so it reports NOT ready — which is the true
    /// answer until a successor is built, and the safe answer if this default is ever reached by a
    /// miswired composition root.
    /// </summary>
    public static InstallationAuthorityVersionRegistry CreateDefault(
        IEnumerable<IInstallationAuthorityV2SignInPath>? signInPaths = null) =>
        new(
        [
            new V1InstallationAuthorityVersionPolicy(),
            new V2InstallationAuthorityVersionPolicy(
                signInPaths ?? Array.Empty<IInstallationAuthorityV2SignInPath>()),
        ]);
}
