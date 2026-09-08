using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.MultiTenancy;

/// <summary>
/// Marks an entity as scoped to a specific tenant. Persistence adapters use
/// this to apply tenant filters, enforce isolation, and surface per-tenant
/// indexes.
/// </summary>
public interface ITenantScoped : Harborline.Foundation.MultiTenancy.ITenantScoped
{
}

/// <summary>
/// Narrower marker: the tenant value must be populated before the entity can
/// be persisted. Persistence adapters reject writes where <see cref="Harborline.Foundation.MultiTenancy.ITenantScoped.TenantId"/>
/// is the default.
/// </summary>
public interface IMustHaveTenant : ITenantScoped, Harborline.Foundation.MultiTenancy.IMustHaveTenant
{
}

/// <summary>
/// Entities that may be tenant-scoped but sometimes represent system-level or
/// cross-tenant records. Persistence adapters apply tenant filters only when
/// <see cref="TenantId"/> is non-null.
/// </summary>
[System.Obsolete("IMayHaveTenant has no active implementors. Use ITenantScoped<T> for "
                 + "entities or TenantSelection for query scope. Will be removed in WS-B "
                 + "(ADR 0085). Per ADR 0084 §3.")]
public interface IMayHaveTenant
{
    /// <summary>The tenant that owns this entity, or null for system-level records.</summary>
    TenantId? TenantId { get; }
}
