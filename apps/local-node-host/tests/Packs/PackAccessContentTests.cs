using Harborline.Api.Foundation.Authorization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 208 slice 3 (L675) — a pack carries ROLE DEFINITIONS and AUTHORIZATION CAPABILITY BINDINGS,
/// and never a GRANT. The binding lands as the PUBLISHER CEILING through the ordinary
/// <see cref="AuthorizationDefinitionWriter"/> and the ordinary
/// <see cref="AuthorizationDefinitionAdmission"/>, so a pack's ceiling is bounded by every rule that
/// admission holds — L628 included: a pack cannot offer the Auditor anything but the platform's own
/// <c>audit:read</c>, which it cannot publish at all. The prohibition on grants is closed twice: BY
/// KIND (<see cref="PackContentKind"/> has no grant member and the codec refuses an undefined kind) and
/// BY SHAPE (a grant smuggled inside another kind's JSON is refused just the same, with the same stable
/// code). A refused pack follows slice 2's invariant: it admits nothing and it removes nothing.
/// </summary>
public sealed class PackAccessContentTests
{
    private const string PackKey = "harborline.access-administration";
    private const string RoleKey = "access.role.grant-reviewer";
    private const string BindingKey = "access.binding.records-read";
    private const string ItemVersion = "1.0.0";
    private const string RoleName = "grant-reviewer";

    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000308");
    private static readonly TenantId OtherTenant = new("bbbbbbbb-0000-0000-0000-000000000308");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly RoleReference PackRole = new(RoleVocabularies.Domain, RoleName);

    private static JsonNode RoleItem(string displayName = "Grant reviewer") =>
        JsonSerializer.SerializeToNode(new { role = RoleName, displayName })!;

    private static JsonNode BindingItem(string operation, params string[] offeredRoles) =>
        JsonSerializer.SerializeToNode(new { operation, scope = "/", offeredRoles })!;

    private const string AssetTypeKey = "access.asset.inspection-kit";
    private const string AssetTypeId = "access.inspection-kit";

    private static JsonNode AssetTypeItem() =>
        JsonSerializer.SerializeToNode(new { id = AssetTypeId, displayName = "Inspection kit", traits = new[] { "maintainable" } })!;

    [Fact(DisplayName = "208 s3: a pack shipping a role definition and a binding that NARROWS the seed's reviewed offer installs, and the binding is the publisher ceiling")]
    public async Task Role_And_Binding_Install_And_The_Binding_Is_The_Publisher_Ceiling()
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem());
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            // {Administrator} is a strict subset of the seed's reviewed offer for records:read
            // ({Administrator, member, node-operator}) -- and it is in practice the ONLY role a pack may
            // offer for a PLATFORM operation, because the seed's own domain roles fail the admission's
            // ownership rule for a pack publisher and a pack's invented role fails the ceiling. A pack
            // role receives capability through the pack's OWN operation.
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        await world.InstallAndActivateAsync("1.0.0");

        var summary = await world.ProjectAsync();

        Assert.Empty(summary.Refusals);
        var role = await world.Vocabulary.ResolveAsync(PackRole);
        Assert.NotNull(role);
        Assert.Equal(RoleOwnerKind.Package, role!.Owner.Kind);
        Assert.Equal(PackKey, role.Owner.OwnerId);
        Assert.False(role.IsSealed);

        // The ceiling: the offered roles the definition carries, read back off ticket 204's store.
        var definition = Assert.Single(await world.Definitions.ListAsync(Tenant));
        Assert.Equal(TeamRolePermissions.RecordsRead, definition.Definition.Operation.Value);
        Assert.Equal(PackKey, definition.Definition.PublisherPackageId);
        Assert.Equal(
            new[] { RoleReference.Administrator },
            definition.Definition.OfferedRoles.Roles.OrderBy(r => r.ToString(), StringComparer.Ordinal));
        // Effective = the ceiling until a tenant narrows it; a tenant can never widen past it.
        Assert.Equal(definition.Definition.OfferedRoles, definition.EffectiveRoles);

        // Re-projecting the same pack on the next boot is a no-op, not a second revision.
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.Equal(1, Assert.Single(await world.Definitions.ListAsync(Tenant)).Definition.Revision);
    }

    [Fact(DisplayName = "208 s3: a pack that widens the Auditor is refused by the existing admission")]
    public async Task A_Pack_That_Widens_The_Auditor_Is_Refused()
    {
        var world = new World();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(Permission.AuditRead, RoleReference.Auditor.ToString()));
        await world.InstallAndActivateAsync("1.0.0");

        var summary = await world.ProjectAsync();

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.AuthorizationCapabilityBinding, refusal.ContentKind);
        Assert.Equal(PackSeedProjector.AccessProjectionRefusedCode, refusal.Code);
        Assert.Empty(await world.Definitions.ListAsync(Tenant));
    }

    [Theory(DisplayName = "208 s3: a pack carrying a grant is refused with a stable code and leaves the store untouched")]
    // BY KIND: the item declares itself an access item and is a grant instance.
    [InlineData(PackContentKind.AuthorizationCapabilityBinding)]
    // BY SHAPE: the same grant smuggled inside another kind's JSON.
    [InlineData(PackContentKind.ViewDefinition)]
    public async Task A_Pack_Carrying_A_Grant_Is_Refused_And_Nothing_Lands(PackContentKind smuggledUnder)
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem());
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        world.Add("access.grant.alice", smuggledUnder, JsonSerializer.SerializeToNode(new
        {
            title = "Perfectly ordinary content",
            entries = new[]
            {
                new { subject = "os:alice#node", role = PackRole.ToString(), scope = "/" },
            },
        })!);
        await world.InstallAndActivateAsync("1.0.0");

        var summary = await world.ProjectAsync();

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal("access.grant.alice", refusal.ContentKey);
        Assert.Equal(PackAuthorizationContentAdmission.GrantInstanceRefusedCode, refusal.Code);
        // Admits nothing: the role and the binding the SAME pack also shipped never landed.
        Assert.Null(await world.Vocabulary.ResolveAsync(PackRole));
        Assert.Empty(await world.Definitions.ListAsync(Tenant));
        // Removes nothing: slice 2's replacement leg is skipped while any refusal stands.
        Assert.Empty(summary.RetractedByKind);
    }

    [Fact(DisplayName = "208 s3: retraction removes both new kinds")]
    public async Task Retraction_Removes_Both_Kinds()
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem());
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        await world.InstallAndActivateAsync("1.0.0");
        await world.ProjectAsync();
        Assert.NotNull(await world.Vocabulary.ResolveAsync(PackRole));

        var deactivation = await world.Installer.DeactivateAsync(Tenant, PackKey, "1.0.0", Now, "test-operator");
        Assert.True(deactivation.Deactivated, deactivation.Error);
        var down = await world.ProjectAsync();

        Assert.Equal(1, down.RetractedByKind.GetValueOrDefault(PackContentKind.RoleDefinition));
        Assert.Equal(1, down.RetractedByKind.GetValueOrDefault(PackContentKind.AuthorizationCapabilityBinding));
        Assert.Null(await world.Vocabulary.ResolveAsync(PackRole));
        // The definition revision is retained (this store is append-only); its BINDING is empty, so the
        // withdrawn pack's capability now offers nothing to anyone.
        Assert.Equal(
            RoleBindingSet.Empty,
            Assert.Single(await world.Definitions.ListAsync(Tenant)).EffectiveRoles);
    }

    [Fact(DisplayName = "208 s3 fix 1: a pack conferring grant:permissions on a role it invented is refused by the seed ceiling")]
    public async Task A_Pack_Cannot_Confer_A_Platform_Only_Operation_On_An_Invented_Role()
    {
        var world = new World();
        // The role definition projects BEFORE the binding, so the invented role really is installed by
        // the time the binding is admitted: nothing but the ceiling stands between this pack and the
        // authority to issue grants.
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem());
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(Permission.GrantPermissions, PackRole.ToString()));
        await world.InstallAndActivateAsync("1.0.0");

        var summary = await world.ProjectAsync();

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.AuthorizationCapabilityBinding, refusal.ContentKind);
        Assert.Equal(PackSeedProjector.AccessProjectionRefusedCode, refusal.Code);
        Assert.Empty(await world.Definitions.ListAsync(Tenant));

        // The stable code the admission refuses under, pinned where it is thrown.
        Assert.Equal(
            "authorization.definition.pack_exceeds_seed_ceiling",
            AuthorizationDefinitionAdmission.SeedCeilingExceededCode);
        Assert.True(AuthorizationDefinitionAdmission.ExceedsReviewedCeiling(
            Definition(Permission.GrantPermissions, PackRole)));
    }

    [Fact(DisplayName = "208 s3 fix 1: a pack is its own ceiling for an operation the platform seed does not define")]
    public void A_Pack_Is_Its_Own_Ceiling_For_Its_Own_Operation()
    {
        // The seed's reviewed table currently covers the whole platform vocabulary exactly (asserted by
        // AccessGrantAuthorizationSeed.InstallAsync), so a pack-owned operation is not yet reachable
        // through the projector. This pins the branch that will carry it: no reviewed offer means no
        // platform ceiling, and the pack publishes its own operation to its own role.
        Assert.Null(AccessGrantAuthorizationSeed.ReviewedOfferFor(
            AuthorizationOperation.Parse("tax:file-return")));
        Assert.False(AuthorizationDefinitionAdmission.ExceedsReviewedCeiling(
            Definition("tax:file-return", PackRole)));
    }

    [Fact(DisplayName = "208 s3 fix 1: a pack binding is declared by the installing tenant only, and retraction narrows exactly that tenant")]
    public async Task A_Pack_Binding_Is_Effective_In_The_Installing_Tenant_Only()
    {
        var world = new World();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()));
        await world.InstallAndActivateAsync("1.0.0");
        await world.ProjectAsync();

        // The definition carries the declaring tenant of the install, so a tenant that never installed
        // the pack does not see it and does not inherit its ceiling.
        Assert.Single(await world.Definitions.ListAsync(Tenant));
        Assert.Empty(await world.Definitions.ListAsync(OtherTenant));

        var deactivation = await world.Installer.DeactivateAsync(Tenant, PackKey, "1.0.0", Now, "test-operator");
        Assert.True(deactivation.Deactivated, deactivation.Error);
        await world.ProjectAsync();

        // Retraction narrows exactly the installing tenant; the other tenant is untouched because the
        // pack was never effective there in the first place.
        Assert.Equal(
            RoleBindingSet.Empty,
            Assert.Single(await world.Definitions.ListAsync(Tenant)).EffectiveRoles);
        Assert.Empty(await world.Definitions.ListAsync(OtherTenant));
    }

    // M11 defect 2 (REVIEW-001 P-1/P-3). A released upgrade re-declares every item at the new revision, so
    // the superseded (key, version) tuple never matches the replacement's. Bindings, roles and asset types
    // project to an identity that does not carry the item version, and the replacement-removal leg must not
    // retract an identity the replacement re-declares. A genuinely dropped identity is still retracted.

    [Fact(DisplayName = "M11 defect 2: an upgrade re-declaring the binding and the role at a NEW item version keeps both")]
    public async Task Upgrade_Redeclaring_Access_Items_At_A_New_Item_Version_Keeps_Them()
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem(), "1.0.0");
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()), "1.0.0");
        await world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await world.ProjectAsync()).Refusals);

        world.Clear();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem(), "1.1.0");
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()), "1.1.0");
        await world.InstallAndActivateAsync("1.1.0");
        var upgraded = await world.ProjectAsync();

        Assert.Empty(upgraded.Refusals);
        Assert.Equal(0, upgraded.RetractedByKind.GetValueOrDefault(PackContentKind.RoleDefinition));
        Assert.Equal(0, upgraded.RetractedByKind.GetValueOrDefault(PackContentKind.AuthorizationCapabilityBinding));
        Assert.NotNull(await world.Vocabulary.ResolveAsync(PackRole));
        var binding = Assert.Single(await world.Definitions.ListAsync(Tenant));
        Assert.Equal(new[] { RoleReference.Administrator }, binding.EffectiveRoles.Roles.ToArray());
        Assert.Equal(1, binding.Definition.Revision);
        // The defect's durable footprint is a 'pack-withdrawn' EMPTY binding revision. None was appended.
        Assert.Equal(0L, binding.BindingRevision);
        Assert.Null(binding.Warning);

        // Replay (the next boot) keeps both: the leg must not re-withdraw on every pass.
        var replay = await world.ProjectAsync();
        Assert.Empty(replay.Refusals);
        Assert.Equal(0, replay.RetractedByKind.GetValueOrDefault(PackContentKind.RoleDefinition));
        Assert.Equal(0, replay.RetractedByKind.GetValueOrDefault(PackContentKind.AuthorizationCapabilityBinding));
        Assert.NotNull(await world.Vocabulary.ResolveAsync(PackRole));
        var replayed = Assert.Single(await world.Definitions.ListAsync(Tenant));
        Assert.Equal(new[] { RoleReference.Administrator }, replayed.EffectiveRoles.Roles.ToArray());
        Assert.Equal(0L, replayed.BindingRevision);
        Assert.Null(replayed.Warning);
    }

    [Fact(DisplayName = "M11 defect 2: an upgrade that DROPS the binding and the role at a new item version still removes both")]
    public async Task Upgrade_Dropping_Access_Items_Removes_Them()
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem(), "1.0.0");
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()), "1.0.0");
        await world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await world.ProjectAsync()).Refusals);

        world.Clear();
        world.Add("access.binding.other", PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(Permission.OrgManageSettings, RoleReference.Administrator.ToString()), "1.1.0");
        await world.InstallAndActivateAsync("1.1.0");
        var upgraded = await world.ProjectAsync();

        Assert.Empty(upgraded.Refusals);
        Assert.Equal(1, upgraded.RetractedByKind.GetValueOrDefault(PackContentKind.RoleDefinition));
        Assert.Equal(1, upgraded.RetractedByKind.GetValueOrDefault(PackContentKind.AuthorizationCapabilityBinding));
        Assert.Null(await world.Vocabulary.ResolveAsync(PackRole));
        var definitions = await world.Definitions.ListAsync(Tenant);
        Assert.Equal(2, definitions.Count);
        var withdrawn = Assert.Single(definitions, row => row.Definition.Operation.Value == TeamRolePermissions.RecordsRead);
        Assert.Equal(RoleBindingSet.Empty, withdrawn.EffectiveRoles);
        // A genuine withdrawal appends exactly one EMPTY binding revision.
        Assert.Equal(1L, withdrawn.BindingRevision);
        Assert.Equal(BindingWarningCode.EmptyBinding, withdrawn.Warning);
        var added = Assert.Single(definitions, row => row.Definition.Operation.Value == Permission.OrgManageSettings);
        Assert.Equal(new[] { RoleReference.Administrator }, added.EffectiveRoles.Roles.ToArray());
        Assert.Equal(0L, added.BindingRevision);
    }

    [Fact(DisplayName = "M11 defect 2: a role re-declared at a new item version survives the upgrade, and is removed when a later version drops it")]
    public async Task Role_Redeclared_Then_Dropped_Across_Upgrades()
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem(), "1.0.0");
        await world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await world.ProjectAsync()).Refusals);

        world.Clear();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem(), "1.1.0");
        await world.InstallAndActivateAsync("1.1.0");
        var kept = await world.ProjectAsync();
        Assert.Empty(kept.Refusals);
        Assert.Equal(0, kept.RetractedByKind.GetValueOrDefault(PackContentKind.RoleDefinition));
        Assert.NotNull(await world.Vocabulary.ResolveAsync(PackRole));

        world.Clear();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()), "1.2.0");
        await world.InstallAndActivateAsync("1.2.0");
        var dropped = await world.ProjectAsync();
        Assert.Empty(dropped.Refusals);
        // Both superseded versions (1.0.0 and 1.1.0) carry the role; the first removal retracts it and the
        // second finds it already gone, so exactly one retraction is counted.
        Assert.Equal(1, dropped.RetractedByKind.GetValueOrDefault(PackContentKind.RoleDefinition));
        Assert.Null(await world.Vocabulary.ResolveAsync(PackRole));
    }

    [Fact(DisplayName = "M11 defect 2: a binding re-declared under a NEW item key for the same operation is the same definition and is kept")]
    public async Task Binding_Moved_To_A_New_Key_Is_Kept()
    {
        var world = new World();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()), "1.0.0");
        await world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await world.ProjectAsync()).Refusals);

        world.Clear();
        world.Add("access.binding.records-read-v2", PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()), "1.1.0");
        await world.InstallAndActivateAsync("1.1.0");
        var moved = await world.ProjectAsync();

        Assert.Empty(moved.Refusals);
        Assert.Equal(0, moved.RetractedByKind.GetValueOrDefault(PackContentKind.AuthorizationCapabilityBinding));
        var binding = Assert.Single(await world.Definitions.ListAsync(Tenant));
        Assert.Equal(new[] { RoleReference.Administrator }, binding.EffectiveRoles.Roles.ToArray());
        Assert.Equal(1, binding.Definition.Revision);
        Assert.Equal(0L, binding.BindingRevision);
        Assert.Null(binding.Warning);
    }

    [Fact(DisplayName = "M11 defect 2: a role and an asset type moved to NEW item keys at the SAME item version are kept")]
    public async Task Role_And_Asset_Type_Moved_To_New_Keys_Are_Kept()
    {
        var world = new World();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem(), "1.0.0");
        world.Add(AssetTypeKey, PackContentKind.AssetTypeDefinition, AssetTypeItem(), "1.0.0");
        await world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await world.ProjectAsync()).Refusals);

        // A content-key rename at an unchanged item version: (newKey, 1.0.0) never matches (oldKey, 1.0.0).
        world.Clear();
        world.Add("access.role.grant-reviewer-v2", PackContentKind.RoleDefinition, RoleItem(), "1.0.0");
        world.Add("access.asset.inspection-kit-v2", PackContentKind.AssetTypeDefinition, AssetTypeItem(), "1.0.0");
        await world.InstallAndActivateAsync("1.1.0");
        var moved = await world.ProjectAsync();

        Assert.Empty(moved.Refusals);
        Assert.Equal(0, moved.RetractedByKind.GetValueOrDefault(PackContentKind.RoleDefinition));
        Assert.Equal(0, moved.RetractedByKind.GetValueOrDefault(PackContentKind.AssetTypeDefinition));
        Assert.Equal(1, moved.AssetTypesAlreadyPresent);
        Assert.Equal(0, moved.AssetTypesRetracted);
        Assert.NotNull(await world.Vocabulary.ResolveAsync(PackRole));
        Assert.NotNull(world.Types.GetSeed(AssetTypeId));
    }

    [Fact(DisplayName = "M11 defect 2: a tenant-narrowed binding re-declared at a NEW item version keeps the narrowing and gains no withdrawal")]
    public async Task Narrowed_Binding_Redeclared_At_A_New_Item_Version_Keeps_The_Narrowing()
    {
        var world = new World();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()), "1.0.0");
        await world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await world.ProjectAsync()).Refusals);
        var narrowing = await world.Installer.NarrowAsync(
            world.Context, PackKey, BindingKey, JsonNode.Parse("""{"offeredRoles":[]}""")!,
            TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now));
        Assert.True(narrowing.Recorded, narrowing.RefusalCode);
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.Equal(RoleBindingSet.Empty, Assert.Single(await world.Definitions.ListAsync(Tenant)).EffectiveRoles);

        world.Clear();
        world.Add(BindingKey, PackContentKind.AuthorizationCapabilityBinding,
            BindingItem(TeamRolePermissions.RecordsRead, RoleReference.Administrator.ToString()), "1.1.0");
        await world.InstallAndActivateAsync("1.1.0");
        // The S-10 re-attach carried the overlay onto the 1.1.0 seed inside the install.
        Assert.Equal(BindingKey, Assert.Single(world.Overrides()).ContentKey);

        for (var pass = 0; pass < 2; pass++)
        {
            var upgraded = await world.ProjectAsync();
            Assert.Empty(upgraded.Refusals);
            Assert.Equal(0, upgraded.RetractedByKind.GetValueOrDefault(PackContentKind.AuthorizationCapabilityBinding));
            var binding = Assert.Single(await world.Definitions.ListAsync(Tenant));
            // The narrowing is applied to the ceiling by the admit loop's narrow-only replace, not by a
            // withdrawal: the offer stays empty and no EMPTY binding revision was ever appended.
            Assert.Equal(RoleBindingSet.Empty, binding.Definition.OfferedRoles);
            Assert.Equal(RoleBindingSet.Empty, binding.EffectiveRoles);
            Assert.Equal(0L, binding.BindingRevision);
            Assert.Null(binding.Warning);
        }
    }

    [Fact(DisplayName = "M11 defect 2 (P-3): a contested NON-OWNER copy of an asset type does not keep the superseded type alive")]
    public async Task Contested_Non_Owner_Asset_Type_Does_Not_Keep_The_Superseded_Type()
    {
        const string otherPackKey = "harborline.access-contested";
        const string replacementTypeId = "access.inspection-kit-v2";
        var world = new World();
        world.Add(AssetTypeKey, PackContentKind.AssetTypeDefinition, AssetTypeItem(), "1.0.0");
        await world.InstallAndActivateAsync("1.0.0");
        Assert.Empty((await world.ProjectAsync()).Refusals);
        Assert.NotNull(world.Types.GetSeed(AssetTypeId));

        // A second pack ships the SAME content key and the same type id; the key is resolved to this pack,
        // so the other pack's copy is a contested non-owner that never projects. Its item version (2.0.0)
        // differs from every version of this pack, so no (key, version) tuple can mask the identity rule.
        world.RecordOwnership(AssetTypeKey, PackKey);
        world.Clear();
        world.Add(AssetTypeKey, PackContentKind.AssetTypeDefinition, AssetTypeItem(), "2.0.0");
        await world.InstallAndActivateAsync("1.0.0", otherPackKey);
        var contested = await world.ProjectAsync();
        Assert.Empty(contested.Refusals);
        Assert.Equal(1, contested.AssetTypesOwnedByOtherPack);

        // This pack's upgrade re-declares the owned key with a DIFFERENT type id: nobody projects the old id.
        world.Clear();
        world.Add(AssetTypeKey, PackContentKind.AssetTypeDefinition,
            JsonSerializer.SerializeToNode(new { id = replacementTypeId, displayName = "Inspection kit v2", traits = new[] { "maintainable" } })!,
            "1.1.0");
        await world.InstallAndActivateAsync("1.1.0");
        var upgraded = await world.ProjectAsync();

        Assert.Empty(upgraded.Refusals);
        Assert.Equal(1, upgraded.AssetTypesSeeded);
        Assert.Equal(1, upgraded.AssetTypesOwnedByOtherPack);
        Assert.Equal(1, upgraded.AssetTypesRetracted);
        Assert.Equal(1, upgraded.RetractedByKind.GetValueOrDefault(PackContentKind.AssetTypeDefinition));
        Assert.Null(world.Types.GetSeed(AssetTypeId));
        Assert.NotNull(world.Types.GetSeed(replacementTypeId));
    }

    [Fact(DisplayName = "M11 defect 2 (P-3): an asset type re-declared at a new item version stays seeded, and is retracted when a later version drops it")]
    public async Task Asset_Type_Redeclared_Then_Dropped_Across_Upgrades()
    {
        var world = new World();
        world.Add(AssetTypeKey, PackContentKind.AssetTypeDefinition, AssetTypeItem(), "1.0.0");
        await world.InstallAndActivateAsync("1.0.0");
        var installed = await world.ProjectAsync();
        Assert.Empty(installed.Refusals);
        Assert.Equal(1, installed.AssetTypesSeeded);
        Assert.NotNull(world.Types.GetSeed(AssetTypeId));

        world.Clear();
        world.Add(AssetTypeKey, PackContentKind.AssetTypeDefinition, AssetTypeItem(), "1.1.0");
        await world.InstallAndActivateAsync("1.1.0");
        var kept = await world.ProjectAsync();
        Assert.Empty(kept.Refusals);
        Assert.Equal(1, kept.AssetTypesAlreadyPresent);
        Assert.Equal(0, kept.AssetTypesRetracted);
        Assert.Equal(0, kept.RetractedByKind.GetValueOrDefault(PackContentKind.AssetTypeDefinition));
        Assert.NotNull(world.Types.GetSeed(AssetTypeId));

        // Replay (the next boot) keeps it: the leg must not hide it again on every pass.
        var replay = await world.ProjectAsync();
        Assert.Empty(replay.Refusals);
        Assert.Equal(1, replay.AssetTypesAlreadyPresent);
        Assert.Equal(0, replay.AssetTypesRetracted);
        Assert.NotNull(world.Types.GetSeed(AssetTypeId));

        world.Clear();
        world.Add(RoleKey, PackContentKind.RoleDefinition, RoleItem(), "1.2.0");
        await world.InstallAndActivateAsync("1.2.0");
        var dropped = await world.ProjectAsync();
        Assert.Empty(dropped.Refusals);
        // Both superseded versions carry the asset type; the first retraction hides it and the second
        // finds it already hidden, so exactly one retraction is counted.
        Assert.Equal(1, dropped.AssetTypesRetracted);
        Assert.Equal(1, dropped.RetractedByKind.GetValueOrDefault(PackContentKind.AssetTypeDefinition));
        Assert.Null(world.Types.GetSeed(AssetTypeId));
    }

    private static AuthorizationCapabilityDefinition Definition(string operationValue, RoleReference role)
    {
        var operation = AuthorizationOperation.Parse(operationValue);
        return new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.Parse("f0f0f0f0-0000-0000-0000-000000000308")),
            PackKey, 1, operation,
            new PermissionAtom(operation, ScopeExpression.Parse("/")), RoleBindingSet.Of(role));
    }

    [Fact(DisplayName = "208 s3: no content kind names a grant")]
    public void No_Content_Kind_Names_A_Grant()
    {
        // Generated from the enum itself, so a future member called "…Grant…" fails here rather than
        // shipping a door the shape fence was written because kinds alone could not close.
        Assert.DoesNotContain(
            Enum.GetNames<PackContentKind>(),
            name => name.Contains("grant", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One tenant, one pack key, the real installer, the real projector, and the real
    /// authorization definition writer + admission over ticket 204's store.</summary>
    private sealed class World
    {
        private readonly KeyPair _keyPair = KeyPair.Generate();
        private readonly PackFileCodec _codec = new();
        private readonly InMemoryPackInstallStore _store = new();
        private readonly List<PackContentSource> _contents = [];
        private readonly ServiceProvider _services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();

        public World()
        {
            Vocabulary = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
            var (grants, configuration) = TestInMemoryAuthorizationStores.Pair();
            Definitions = configuration;
            var writer = new AuthorizationDefinitionWriter(
                configuration,
                configuration,
                new AuthorizationDefinitionAdmission(Vocabulary),
                new AuthorizationCapabilityBindingAdmission(),
                TestAuthorization.AllowGate(),
                grants);
            Installer = new PackInstaller(
                new PackVerifier(new Ed25519Verifier(), _codec),
                _store,
                new WorkflowRefusingPackContentAdmission(),
                new InMemoryPackInstallAudit(),
                TestAuthorization.AllowGate());
            Projector = new PackSeedProjector(
                _store,
                _services.GetRequiredService<IEntityTypeRegistry>(),
                NullLogger<PackSeedProjector>.Instance,
                time: TimeProvider.System,
                roleVocabulary: Vocabulary,
                authorizationDefinitions: writer);
            Context = new PackInstallContext(
                Tenant,
                new InMemoryPackTrustStore(
                    [new PackTrustRoot(TrustScope.OwnRoster, _keyPair.PrincipalId, 1, TrustRootStatus.Current)]),
                PackRevocationList.Empty,
                AdmittedInstant.FromRecordedAct(Now),
                TimeSpan.FromDays(30),
                Principal: "test-operator");
        }

        public InMemoryRoleVocabulary Vocabulary { get; }

        public IAuthorizationDefinitionCatalogueReader Definitions { get; }

        public PackInstaller Installer { get; }

        public PackSeedProjector Projector { get; }

        public PackInstallContext Context { get; }

        public IEntityTypeRegistry Types => _services.GetRequiredService<IEntityTypeRegistry>();

        public void Add(string key, PackContentKind kind, JsonNode content, string itemVersion = ItemVersion) =>
            _contents.Add(new PackContentSource(key, kind, itemVersion, content));

        /// <summary>Starts the next version's contents from nothing (an upgrade ships its own item set).</summary>
        public void Clear() => _contents.Clear();

        public IReadOnlyList<PackTenantOverride> Overrides() => _store.GetOverrides(Tenant, PackKey);

        /// <summary>Records the tenant's owning-pack choice for a contested content key.</summary>
        public void RecordOwnership(string contentKey, string owningPackKey) =>
            _store.RecordKeyOwnership(Tenant, contentKey, owningPackKey);

        public Task<PackSeedProjectionSummary> ProjectAsync() =>
            ((IPackSeedProjector)Projector).ProjectActivePacksAsync(Tenant);

        public async Task InstallAndActivateAsync(string packVersion, string packKey = PackKey)
        {
            var exporter = new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                _codec,
                timeProvider: TimeProvider.System);
            var export = await exporter.ExportAsync(
                new PackExportRequest(
                    Key: packKey,
                    Version: packVersion,
                    Name: "Access administration",
                    Description: "Exercises pack role definitions and capability bindings.",
                    ScopeTier: PackScopeTier.Horizontal,
                    Contents: _contents,
                    Dependencies: Array.Empty<PackDependencyRef>(),
                    CapabilityRequirements: Array.Empty<string>(),
                    Epoch: 1,
                    Dcp: DomainComplianceProfile.General("access-administration-test-author")),
                new Ed25519Signer(_keyPair));
            Assert.True(export.Succeeded, string.Join(
                "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));

            var install = await Installer.InstallAsync(export.FileBytes!, Context);
            Assert.True(install.Installed, string.Join("; ", install.RefusalCodes));
            PlatformPackTestPreload.Activate(_store, Tenant);
            var activation = Installer.Activate(Tenant, packKey, packVersion, Now, "test-operator");
            Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");
        }
    }
}
