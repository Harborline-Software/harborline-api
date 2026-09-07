using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// Opaque authority for projecting one exact activated pack version. Only the pack installer can mint
/// production instances; test assemblies are friends so adversarial mismatch cases remain provable.
/// </summary>
public sealed class PackProjectionAuthority
{
    private int _lifetime;
    internal PackProjectionAuthority(
        AuthorizationDecision decision,
        string packId,
        string packVersion,
        TenantId tenant,
        ActorId principal,
        DateTimeOffset activationInstant,
        Guid? nonce = null,
        DateTimeOffset? requestAt = null,
        DateTimeOffset? decidedAt = null)
    {
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packVersion);
        PackId = packId;
        PackVersion = packVersion;
        Tenant = tenant;
        Principal = principal;
        ActivationInstant = activationInstant;
        Nonce = nonce ?? Guid.NewGuid();
        RequestAt = requestAt ?? decision.Request.At;
        DecidedAt = decidedAt ?? decision.DecidedAt;
        Verdict = decision.Verdict;
        Operation = decision.Request.Act.Operation.Value;
        TargetKind = decision.Request.Target.RecordKind;
        TargetId = decision.Request.Target.RecordId;
        DecisionTenant = decision.Request.Tenant;
        DecisionPrincipal = decision.Request.Principal;
        DerivationIds = decision.Derivations
            .SelectMany(derivation => new[] { derivation.GrantId, derivation.DefinitionId })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private PackProjectionAuthority(PackProjectionAdmission admission)
    {
        PackId = admission.PackId;
        PackVersion = admission.PackVersion;
        Tenant = admission.Tenant;
        Principal = admission.Principal;
        ActivationInstant = admission.Instant;
        Nonce = Guid.NewGuid();
        RequestAt = admission.Instant;
        DecidedAt = admission.Instant;
        Verdict = AuthorizationVerdict.Allowed;
        Operation = Permission.PackagesOperate;
        TargetKind = "pack";
        TargetId = admission.PackId;
        DecisionTenant = admission.Tenant;
        DecisionPrincipal = admission.Principal;
        DerivationIds = admission.DerivationIds.ToArray();
    }

    internal AuthorizationDecision? Decision { get; }
    public string PackId { get; }
    public string PackVersion { get; }
    public TenantId Tenant { get; }
    internal ActorId Principal { get; }
    public DateTimeOffset ActivationInstant { get; }
    public Guid Nonce { get; }
    internal DateTimeOffset RequestAt { get; }
    internal DateTimeOffset DecidedAt { get; }
    internal AuthorizationVerdict Verdict { get; }
    internal string Operation { get; }
    internal string TargetKind { get; }
    internal string TargetId { get; }
    internal TenantId DecisionTenant { get; }
    internal ActorId DecisionPrincipal { get; }
    internal IReadOnlyList<string> DerivationIds { get; }

    /// <summary>Refuses malformed or foreign carried authority before a projector may read a store.</summary>
    internal void RequireValid()
    {
        if (Verdict != AuthorizationVerdict.Allowed)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.DecisionDenied);
        if (Operation != Permission.PackagesOperate)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.OperationMismatch);
        if (!string.Equals(TargetKind, "pack", StringComparison.Ordinal))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TargetMismatch);
        if (!string.Equals(TargetId, PackId, StringComparison.Ordinal))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TargetMismatch);
        if (DecisionTenant != Tenant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TenantMismatch);
        if (DecisionPrincipal != Principal)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.PrincipalMismatch);
        if (string.IsNullOrWhiteSpace(Principal.Value))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.PrincipalMismatch);
        if (RequestAt != ActivationInstant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.InstantMismatch);
        if (DecidedAt != ActivationInstant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.InstantMismatch);
    }

    public void EnsureUsable()
    {
        RequireValid();
        if (Volatile.Read(ref _lifetime) != 0)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.Replayed);
    }

    internal void Retire() => Interlocked.Exchange(ref _lifetime, 2);

    internal static PackProjectionAuthority FromAdmission(PackProjectionAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.Projected)
            throw new InvalidOperationException("A completed pack projection admission cannot be replayed.");
        if (string.IsNullOrWhiteSpace(admission.Tenant.Value)
            || string.IsNullOrWhiteSpace(admission.PackId)
            || string.IsNullOrWhiteSpace(admission.PackVersion)
            || admission.Instant == default
            || admission.DerivationIds is null
            || admission.DerivationIds.Count == 0
            || admission.DerivationIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Pack projection admission evidence is incomplete.");
        return new PackProjectionAuthority(admission);
    }
}

/// <summary>The immutable source declaration stamped into a definition projected from a pack.</summary>
public sealed record PackProjectionSource
{
    public PackProjectionSource(string packId, string packVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packVersion);
        PackId = packId;
        PackVersion = packVersion;
    }

    public string PackId { get; }
    public string PackVersion { get; }
}

public static class PackProjectionAuthorityCodes
{
    public const string DecisionDenied = "pack.projection.authority.denied";
    public const string OperationMismatch = "pack.projection.authority.operation_mismatch";
    public const string TargetMismatch = "pack.projection.authority.target_mismatch";
    public const string TenantMismatch = "pack.projection.authority.tenant_mismatch";
    public const string PrincipalMismatch = "pack.projection.authority.principal_mismatch";
    public const string InstantMismatch = "pack.projection.authority.instant_mismatch";
    public const string SourceMismatch = "pack.projection.authority.source_mismatch";
    public const string WriteInstantMismatch = "pack.projection.authority.write_instant_mismatch";
    public const string Replayed = "pack.projection.authority.replayed";
}

public sealed class PackProjectionAuthorityException(string code)
    : InvalidOperationException($"Pack projection authority refused: {code}.")
{
    public string Code { get; } = code;
}
