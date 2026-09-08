using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.MultiTenancy;
using System.Text.Json.Serialization;

namespace Harborline.Api.Blocks.AccessGrant;

public enum GrantStatus { Active = 0, Revoked = 1 }
public enum GrantSourceKind { Bootstrap = 0, Invitation = 1, Manual = 2, Workflow = 3, Ticket = 4 }
public enum GranterKind { Installer = 0, Person = 1 }

public readonly record struct GrantReason
{
    [JsonConstructor]
    public GrantReason(string code, string? reference = null)
    {
        if (!GrantReasonCodes.All.Contains(code, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown grant reason code '{code}'.", nameof(code));
        if (reference is { Length: > 128 })
            throw new ArgumentException("A grant reason reference cannot exceed 128 characters.", nameof(reference));
        Code = code;
        Reference = reference;
    }

    public string Code { get; }
    public string? Reference { get; }
}

public sealed record GrantProvenance(GrantSourceKind Source, GrantReason Reason, ActorId Approver);

public readonly record struct GrantValidity
{
    [JsonConstructor]
    public GrantValidity(DateTimeOffset validFrom, DateTimeOffset? validTo = null)
    {
        if (validTo is { } end && end <= validFrom)
            throw new ArgumentException("Grant validity end must be later than its start.", nameof(validTo));
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    public DateTimeOffset ValidFrom { get; }
    public DateTimeOffset? ValidTo { get; }
    public bool Contains(DateTimeOffset at) => at >= ValidFrom && (ValidTo is null || at < ValidTo.Value);
}

public sealed record GrantRevocation(ActorId RevokedBy, DateTimeOffset RevokedAt, GrantReason Reason);

public sealed record GrantValidityChangeEvidence(ActorId ChangedBy, GrantReason Reason);

public sealed record AccessGrant : IMustHaveTenant
{
    public AccessGrant(GrantId grantId, TenantId tenantId, ActorId subject, RoleReference role,
        ScopeExpression scope, GrantResidency residency, GrantValidity validity,
        GranterKind granterKind, ActorId grantedBy, DateTimeOffset grantedAt,
        GrantProvenance grant, DateTimeOffset lastReviewedAt,
        GrantStatus status = GrantStatus.Active, GrantRevocation? revocation = null,
        GrantValidityChangeEvidence? validityChange = null, ActorId? lastReviewedBy = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(grant);
        if (string.IsNullOrWhiteSpace(subject.Value)) throw new ArgumentException("A grant subject is required.", nameof(subject));
        if (grant.Reason.Code != GrantReasonCodes.Bootstrap && granterKind != GranterKind.Person)
            throw new ArgumentException("Every post-bootstrap grant requires a person granter.", nameof(granterKind));
        if (granterKind == GranterKind.Person && string.IsNullOrWhiteSpace(grantedBy.Value))
            throw new ArgumentException("A person granter id is required.", nameof(grantedBy));
        if (lastReviewedAt == default) throw new ArgumentException("A grant review timestamp is required.", nameof(lastReviewedAt));
        if ((status == GrantStatus.Revoked) != (revocation is not null))
            throw new ArgumentException("Revoked status and revocation evidence must be present together.", nameof(revocation));

        GrantId = grantId; TenantId = tenantId; Subject = subject; Role = role;
        Scope = scope; Residency = residency; Validity = validity;
        GranterKind = granterKind; GrantedBy = grantedBy; GrantedAt = grantedAt;
        Grant = grant; LastReviewedAt = lastReviewedAt; Status = status; Revocation = revocation;
        ValidityChange = validityChange; LastReviewedBy = lastReviewedBy;
    }

    public GrantId GrantId { get; init; }
    public TenantId TenantId { get; init; }
    public ActorId Subject { get; init; }
    public RoleReference Role { get; init; }
    /// <summary><c>/</c> is the whole tenant; <c>/records/&lt;recordId&gt;</c> is one record.</summary>
    public ScopeExpression Scope { get; init; }
    public GrantResidency Residency { get; init; }
    public GrantValidity Validity { get; init; }
    public GranterKind GranterKind { get; init; }
    public ActorId GrantedBy { get; init; }
    public DateTimeOffset GrantedAt { get; init; }
    public GrantProvenance Grant { get; init; }
    public DateTimeOffset LastReviewedAt { get; init; }
    public GrantStatus Status { get; init; }
    public GrantRevocation? Revocation { get; init; }
    public GrantValidityChangeEvidence? ValidityChange { get; init; }
    public ActorId? LastReviewedBy { get; init; }
    public bool IsActiveAt(DateTimeOffset at) =>
        at >= GrantedAt
        && Validity.Contains(at)
        && (Revocation is null || at < Revocation.RevokedAt);
}

public readonly record struct GrantId(Guid Value)
{
    public static GrantId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public enum GrantResidency { Cache = 0, OnlineOnly = 1 }
