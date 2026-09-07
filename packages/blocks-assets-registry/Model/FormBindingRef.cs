using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// A pinned reference to a form definition (ADR 0055 pattern): the content-addressed
/// <see cref="FormDefinitionId"/> plus the <see cref="SemanticVersion"/> the referrer was
/// authored against.
/// </summary>
/// <remarks>
/// <para>
/// The <b>type</b> declares which form defines its dynamic attributes; each <b>entity</b>
/// pins the exact version its captured values were authored under
/// (<see cref="EntityTypeDescriptor.PropertyFormBinding"/> vs <see cref="RegistryEntity.PropertyForm"/>).
/// Because identity of a <see cref="FormDefinition"/> is the tuple <c>(Id, Version)</c>, an
/// entity authored under v1 stays valid when the type's property-form advances to v2 — type
/// upgrades are explicit migrations, never silent (D-D).
/// </para>
/// <para>
/// <b>Tenant-authorization is NOT conveyed by this ref (F2 invariant 2).</b> Holding a
/// <see cref="FormDefinitionId"/> — a content hash — confers no read access. A binding is only
/// ever reachable through a tenant-scoped registry row the caller's tenant owns; the resolver
/// enforces that (<see cref="Services.IEntityTypeRegistry.TryResolvePropertyFormAsync"/>).
/// </para>
/// </remarks>
/// <param name="Definition">The content-addressed form definition id.</param>
/// <param name="PinnedVersion">The exact definition version this reference is pinned to.</param>
public sealed record FormBindingRef(FormDefinitionId Definition, SemanticVersion PinnedVersion);
