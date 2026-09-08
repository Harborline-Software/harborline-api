using System.Security.Cryptography;
using System.Text;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Blocks.Workflow.Durable;

namespace Harborline.Api.Blocks.AccessGrant;

public sealed record AuthorizationSeedProfile(bool IncludeDevelopmentGrants)
{
    public static AuthorizationSeedProfile Production { get; } = new(false);
    public static AuthorizationSeedProfile Development { get; } = new(true);
}

/// <summary>The single compatibility seed translating current operations into definition-led role offers.</summary>
internal sealed class AccessGrantAuthorizationSeed(
    AuthorizationDefinitionWriter writer,
    AuthorizationConfigurationStateReader states,
    IGrantStore grants)
{
    /// <summary>The deterministic, tenant-owned role for one verified historical admission.</summary>
    internal static RoleDefinition AdmissionMigrationRole(Guid id, TenantId tenant) =>
        RoleDefinition.CreateTenantRole(new(id), "roster-admission-" + id.ToString("N"), "Migrated admission", tenant);

    public const string PackageId = "harborline.access-grant";
    public const string SchedulerPrincipal = "sys.scheduler";
    public const string DevIndexerPrincipal = "sys.dev-indexer";
    public const string DevWorkflowSeederPrincipal = "sys.dev-workflow-seeder";

    /// <summary>
    /// The single-operator DESKTOP principal — the party a desktop-plane request carries when no
    /// selected-session principal is bound (<c>NodeCallerParty.OperatorParty</c>). It is the founder on a
    /// solo install, and it is the subject of the one seeded holding below.
    /// </summary>
    /// <remarks>
    /// Must equal <c>ActiveTeamAuthorizationContext.LocalUserId</c> in the node host; that host cannot be referenced
    /// from this package, so the two are pinned equal by
    /// <c>LifecycleUnlockGrantEndToEndTests.The_seeded_node_operator_principal_is_the_desktop_caller_party</c>.
    /// </remarks>
    public const string NodeOperatorPrincipal = "local";
    internal const string SchedulerGrantSource = "authorization-seed:system-scheduler";
    internal const string DevIndexerGrantSource = "authorization-seed:system-dev-indexer";
    internal const string DevWorkflowSeederGrantSource = "authorization-seed:system-dev-workflow-seeder";
    internal const string NodeOperatorGrantSource = "authorization-seed:node-operator";
    internal static ActorId AdditiveSeedPrincipal { get; } =
        new("installer:authorization-additive-seed");
    public static RoleReference MemberRole { get; } = new(RoleVocabularies.Domain, "member");
    public static RoleReference SchedulerRole { get; } = new(RoleVocabularies.Domain, "system-scheduler");
    public static RoleReference DevIndexerRole { get; } = new(RoleVocabularies.Domain, "system-dev-indexer");
    public static RoleReference DevWorkflowSeederRole { get; } = new(RoleVocabularies.Domain, "system-dev-workflow-seeder");

    /// <summary>
    /// The desktop node operator's role. It offers exactly the operations the desktop route families
    /// resolve at the gate, so the solo founder's holdings travel as reviewable grants without conferring
    /// anything else. It is deliberately NOT <see cref="RoleReference.Administrator"/>: an Administrator
    /// grant would seal the definition bootstrap path and disqualify the installation's bootstrap-claim
    /// redemption.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The converted families and the operations each contributes: mode entry (<c>workshop:unlock</c>,
    /// ticket 205 slice 2); packs and feeds (<c>packages:operate</c>, <c>packages:author</c>, slice 3); and
    /// the record-scoped route families of slice 4 — contacts (<c>contacts:read/create/write/archive</c>),
    /// invoices, bank accounts and entities (<c>records:read</c>, <c>records:write</c>), the ledger
    /// (<c>ledger:post</c>), form definitions (<c>forms:author</c>), scheduling
    /// (<c>scheduling:read/author/operate</c>), spatial frames (<c>spatial:read</c>) and the
    /// authorization-admin surface (<c>org:manage-settings</c>). On a desktop install the only caller of
    /// those routes is the solo founder on the desktop plane, so without the offer every one of them is a
    /// blanket denial.
    /// </para>
    /// <para>
    /// Deliberately NOT offered to it, because no converted route family performs them:
    /// <c>packages:publish</c> (signs for the world), <c>packages:install</c> (the web-plane registry act),
    /// <c>members:*</c>, <c>grant:permissions</c>, <c>org:transfer-ownership</c>,
    /// <c>provider:configure-*</c>, <c>telemetry:export</c> and <c>org:branding-write</c>. Adding an
    /// operation here is a reviewed row in <c>ReviewedOffers</c>, never a consequence of a route growing a
    /// new check.
    /// </para>
    /// <para>
    /// Ticket 217 adds one: <c>audit:read</c>, because that ticket converts the audit-events route family to
    /// resolve at the gate and the solo founder is its only desktop caller.
    /// </para>
    /// </remarks>
    public static RoleReference NodeOperatorRole { get; } = new(RoleVocabularies.Domain, "node-operator");
    public static RoleDefinition MemberDefinition { get; } = RoleDefinition.CreatePackageRole(
        new RoleDefinitionId(new Guid("3b69e3cb-ec5e-4ad7-8c90-16ebc1896103")),
        "member", "Member", PackageId);
    public static RoleDefinition SchedulerDefinition { get; } = RoleDefinition.CreatePackageRole(
        new RoleDefinitionId(new Guid("3b69e3cb-ec5e-4ad7-8c90-16ebc1896104")),
        SchedulerRole.Name, "Workflow scheduler", PackageId);
    public static RoleDefinition DevIndexerDefinition { get; } = RoleDefinition.CreatePackageRole(
        new RoleDefinitionId(new Guid("3b69e3cb-ec5e-4ad7-8c90-16ebc1896105")),
        DevIndexerRole.Name, "Development search indexer", PackageId);
    public static RoleDefinition DevWorkflowSeederDefinition { get; } = RoleDefinition.CreatePackageRole(
        new RoleDefinitionId(new Guid("3b69e3cb-ec5e-4ad7-8c90-16ebc1896106")),
        DevWorkflowSeederRole.Name, "Development workflow seeder", PackageId);
    public static RoleDefinition NodeOperatorDefinition { get; } = RoleDefinition.CreatePackageRole(
        new RoleDefinitionId(new Guid("3b69e3cb-ec5e-4ad7-8c90-16ebc1896107")),
        NodeOperatorRole.Name, "Node operator", PackageId);
    internal static IReadOnlyList<RoleDefinition> RoleDefinitions { get; } =
    [
        MemberDefinition, SchedulerDefinition, DevIndexerDefinition, DevWorkflowSeederDefinition,
        NodeOperatorDefinition,
    ];

    // Reviewed, checked-in policy. New operations receive no role merely because a legacy composition grew;
    // adding one requires an explicit row and review here.
    private static readonly IReadOnlyDictionary<string, RoleBindingSet> ReviewedOffers =
        new Dictionary<string, RoleBindingSet>(StringComparer.Ordinal)
        {
            // `nodeOperator: true` marks a row the desktop founder must keep holding once its route family
            // resolves at the gate — see NodeOperatorRole above for which families those are and why.
            [Permission.ContactsRead] = Roles(member: true, nodeOperator: true),
            [Permission.ContactsCreate] = Roles(member: true, nodeOperator: true),
            [Permission.ContactsWrite] = Roles(member: true, nodeOperator: true),
            [Permission.ContactsArchive] = Roles(member: true, nodeOperator: true),
            [Permission.CalendarRead] = Roles(member: true),
            [Permission.CalendarCreate] = Roles(member: true),
            [Permission.CalendarWrite] = Roles(member: true),
            [Permission.CalendarArchive] = Roles(member: true),
            [Permission.CommsRead] = Roles(member: true),
            [Permission.CommsAppend] = Roles(member: true),
            [Permission.GlRead] = Roles(member: true),
            [Permission.GlPost] = Roles(),
            [Permission.FinancialPeriodOverrideSoftClose] = Roles(),
            [Permission.SpatialRead] = Roles(member: true, nodeOperator: true),
            [Permission.MembersAdmit] = Roles(),
            [Permission.MembersRevoke] = Roles(),
            [Permission.MembersSetRole] = Roles(),
            [Permission.GrantPermissions] = Roles(),
            [Permission.OrgTransferOwnership] = Roles(),
            [Permission.ProviderConfigureIdentity] = Roles(),
            [Permission.ProviderConfigureEmail] = Roles(),
            [Permission.ProviderConfigureStorage] = Roles(),
            [Permission.ProviderConfigurePayments] = Roles(),
            [Permission.ProviderConfigureBankFeed] = Roles(),
            [Permission.ProviderConfigureTelemetry] = Roles(),
            [Permission.ProviderReadConfig] = Roles(),
            // Ticket 217 / L628 -- the ONE row that offers the sealed platform Auditor role, and the
            // whole of what Auditor holds. Every other read used to offer it (the read-verb heuristic in
            // AuthorizationDefinitionAdmission), which is why Auditor could read contacts, the calendar,
            // stories, comms, the ledger, spatial frames, scheduling, records and the provider config.
            // Adding `auditor: true` to any other row is now refused at admission, not merely unreviewed.
            // `nodeOperator: true` because ticket 217 also converts the audit-events route family to resolve
            // at the gate, and on a desktop install its only caller is the solo founder -- without the offer
            // the audit viewer is a blanket denial. Holding audit:read alongside everything else is not the
            // Auditor's least privilege; it is the operator's, and the two are different reviewed rows.
            [Permission.AuditRead] = Roles(auditor: true, nodeOperator: true),
            // Ticket 212 slice 3 -- reading the trace of your OWN decision. `member: true` because 163's
            // "Why can I do this?" is a question every person asks about their own act, and the read
            // refuses on the entry's own scope when the decision was about someone else. Deliberately NOT
            // `auditor: true`: the Auditor reads another person's trace through `audit:read`, the one
            // definition that may offer it, so this row does not widen the sealed role.
            [Permission.AuditTraceRead] = Roles(member: true, nodeOperator: true),
            [Permission.TelemetryExport] = Roles(),
            [Permission.OrgManageSettings] = Roles(nodeOperator: true),
            [Permission.OrgBrandingWrite] = Roles(),
            // The pack and feed route family (ticket 205 slice 3) resolves these two at the gate. On a
            // desktop install the only caller is the solo founder on the desktop plane, so the node
            // operator must hold them or every pack route is a blanket denial. packages:publish and
            // packages:install are deliberately NOT offered to it: publishing signs for the world and
            // install-from-registry is a web-plane act, neither of which the desktop route family performs.
            [Permission.PackagesAuthor] = RoleBindingSet.From(
                [RoleReference.Administrator, NodeOperatorRole]),
            [Permission.PackagesPublish] = Roles(),
            [Permission.PackagesInstall] = Roles(),
            [Permission.PackagesOperate] = RoleBindingSet.From(
                [RoleReference.Administrator, NodeOperatorRole]),
            [Permission.FormsAuthor] = Roles(nodeOperator: true),
            [Permission.SchedulingRead] = Roles(member: true, nodeOperator: true),
            [Permission.SchedulingAuthor] = Roles(nodeOperator: true),
            [Permission.SchedulingOperate] = Roles(nodeOperator: true),
            // Ticket 213 / L646 -- the subject-consent record's read and its four transitions. Offered to
            // Administrator (through Roles) and the node operator, because on a desktop install the solo
            // founder is the only caller of the consent routes and without the offer every one of them is a
            // blanket denial -- the same reasoning the audit-events family used. NOT offered to `member`:
            // recording what a subject consented to is an administrative act over the install's records,
            // not an ordinary member's. NOT offered to the Auditor: the Auditor reads the trail and changes
            // nothing, and since ticket 217 a definition that offers Auditor for any operation other than
            // `audit:read` is refused at admission -- so `auditor: true` here would fail the seed, not
            // widen it.
            [Permission.ConsentRead] = Roles(nodeOperator: true),
            [Permission.ConsentWrite] = Roles(nodeOperator: true),
            [TeamRolePermissions.LedgerPost] = Roles(nodeOperator: true),
            [TeamRolePermissions.MembersManage] = Roles(),
            [TeamRolePermissions.RecordsWrite] = Roles(member: true, nodeOperator: true),
            [TeamRolePermissions.RecordsRead] = Roles(member: true, nodeOperator: true),
            // Install-wide mode-entry (ADR 0144 AD.1): Administrator, plus the desktop node operator — the
            // solo founder, through NodeOperatorRole. A member does not re-open Build.
            [Permission.WorkshopUnlock] = RoleBindingSet.From(
                [RoleReference.Administrator, NodeOperatorRole]),
        };

    /// <summary>
    /// (L675) The reviewed offer this seed publishes for <paramref name="operation"/>, or null when the
    /// platform defines no offer for it. This table is the hand-reviewed ceiling for a platform
    /// operation, so a pack that binds one may narrow it but never widen it — see
    /// <see cref="AuthorizationDefinitionAdmission.ExceedsReviewedCeiling"/>. Null means the operation is
    /// the publisher's own, and the publisher is then its own ceiling.
    /// </summary>
    internal static RoleBindingSet? ReviewedOfferFor(AuthorizationOperation operation) =>
        ReviewedOffers.TryGetValue(operation.Value, out var reviewed) ? reviewed : null;

    internal static IReadOnlyList<AuthorizationCapabilityDefinition> AdditiveSystemDefinitions { get; } =
        CreateSystemPrincipalDefinitions().ToArray();

    internal static IReadOnlyList<AuthorizationCapabilityDefinition> FoundingDefinitions { get; } =
        PermissionVocabulary.Operations.Select(operation => new AuthorizationCapabilityDefinition(
            DefinitionIdFor(operation), PackageId, 1, operation,
            new PermissionAtom(operation, ScopeExpression.Parse("/")), ReviewedOffers[operation.Value]))
        .ToArray();

    internal async ValueTask InstallAsync(
        TenantId tenant,
        DateTimeOffset at,
        AuthorizationSeedProfile profile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var operations = PermissionVocabulary.Operations.Select(operation => operation.Value).ToHashSet(StringComparer.Ordinal);
        if (!operations.SetEquals(ReviewedOffers.Keys))
            throw new InvalidOperationException("Authorization seed offers must exactly cover the operation catalogue.");

        var missingFounding = await FindMissingAsync(FoundingDefinitions, ct).ConfigureAwait(false);

        if (missingFounding.Count > 0 && await grants.HasAdministratorGrantEverAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The authorization-definition bootstrap path is permanently sealed after an Administrator grant has ever existed.");
        }

        var foundingAuthority = new Harborline.Api.Foundation.Authorization.AuthorizationWriteContext(
            new ActorId("installer:authorization-definition-seed"),
            tenant,
            at);
        foreach (var definition in missingFounding)
        {
            var request = foundingAuthority.Request(
                AuthorizationOperation.Parse(Permission.GrantPermissions),
                "grant",
                definition.DefinitionId.Value.ToString());
            var decision = Harborline.Api.Foundation.Authorization.AuthorizationDecision.CreateBootstrap(
                request,
                "administrator-grant-history-absent");
            await writer.WriteBootstrapAsync(
                new InstallAuthorizationDefinition(definition),
                MintPlatformDefinitionBootstrap(decision),
                ct).ConfigureAwait(false);
        }

        var additiveAuthority = new Harborline.Api.Foundation.Authorization.AuthorizationWriteContext(
            AdditiveSeedPrincipal,
            tenant,
            at);
        foreach (var definition in await FindMissingAsync(AdditiveSystemDefinitions, ct).ConfigureAwait(false))
        {
            await writer.WriteAdditiveSeedRevisionAsync(
                new InstallAuthorizationDefinition(definition), additiveAuthority, ct).ConfigureAwait(false);
        }

        await EnsureSystemGrantAsync(
            tenant, at, SchedulerPrincipal, SchedulerRole, SchedulerGrantSource, ct).ConfigureAwait(false);
        // The founder's workshop:unlock holding. It used to live in the flat TeamMembership.Permissions set
        // the node bootstrap wrote; the unlock decision now reads the grant closure, so the holding is
        // seeded HERE, where the decision reads it. Issued after the founding definitions above, so the
        // administrator-grant seal is never tripped by it (and it is not an Administrator grant anyway).
        await EnsureSystemGrantAsync(
            tenant, at, NodeOperatorPrincipal, NodeOperatorRole, NodeOperatorGrantSource, ct)
            .ConfigureAwait(false);
        if (profile.IncludeDevelopmentGrants)
        {
            await EnsureSystemGrantAsync(
                tenant, at, DevIndexerPrincipal, DevIndexerRole, DevIndexerGrantSource, ct).ConfigureAwait(false);
            await EnsureSystemGrantAsync(
                tenant, at, DevWorkflowSeederPrincipal, DevWorkflowSeederRole,
                DevWorkflowSeederGrantSource, ct).ConfigureAwait(false);
        }
    }

    internal static bool IsAdditiveSystemDefinition(AuthorizationCapabilityDefinition definition) =>
        AdditiveSystemDefinitions.Any(expected => expected == definition);

    private async ValueTask<List<AuthorizationCapabilityDefinition>> FindMissingAsync(
        IEnumerable<AuthorizationCapabilityDefinition> definitions,
        CancellationToken ct)
    {
        var missing = new List<AuthorizationCapabilityDefinition>();
        foreach (var definition in definitions)
        {
            var existing = (await states.ReadStateAsync(definition.DefinitionId, ct: ct).ConfigureAwait(false)).Definition;
            if (existing is null)
            {
                missing.Add(definition);
                continue;
            }
            if (existing.PublisherPackageId != definition.PublisherPackageId
                || existing.Operation != definition.Operation
                || existing.Atom != definition.Atom
                || !existing.OfferedRoles.Equals(definition.OfferedRoles))
            {
                throw new InvalidOperationException(
                    $"Authorization seed definition '{definition.DefinitionId.Value}' conflicts with '{definition.Operation}'.");
            }
        }
        return missing;
    }

    private static IEnumerable<AuthorizationCapabilityDefinition> CreateSystemPrincipalDefinitions()
    {
        var recordsWrite = AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);
        yield return new AuthorizationCapabilityDefinition(
            SystemDefinitionIdFor("scheduler-and-dev-indexer", recordsWrite), PackageId, 1, recordsWrite,
            new PermissionAtom(recordsWrite, ScopeExpression.Parse("/")),
            RoleBindingSet.Of(SchedulerRole, DevIndexerRole));

        var ledgerPost = AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost);
        yield return new AuthorizationCapabilityDefinition(
            SystemDefinitionIdFor("scheduler", ledgerPost), PackageId, 1, ledgerPost,
            new PermissionAtom(ledgerPost, ScopeExpression.Parse("/")),
            RoleBindingSet.Of(SchedulerRole));

        var schedulingAuthor = AuthorizationOperation.Parse(Permission.SchedulingAuthor);
        yield return new AuthorizationCapabilityDefinition(
            SystemDefinitionIdFor("dev-workflow-seeder", schedulingAuthor), PackageId, 1, schedulingAuthor,
            new PermissionAtom(schedulingAuthor, ScopeExpression.Parse("/")),
            RoleBindingSet.Of(DevWorkflowSeederRole));
    }

    private async ValueTask EnsureSystemGrantAsync(
        TenantId tenant,
        DateTimeOffset at,
        string principal,
        RoleReference role,
        string sourceReference,
        CancellationToken ct)
    {
        // A revoked seed grant remains present by source reference and is deliberately never resurrected.
        // This is what makes the checked-in system principal visible and tenant-revocable like every other grant.
        if (await grants.FindBySourceReferenceAsync(tenant, sourceReference, ct).ConfigureAwait(false) is not null)
            return;

        var grant = SeedGrantFor(tenant, at, principal, role, sourceReference).Grant;
        await grants.AppendAsync(tenant, grant, sourceReference, ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<InstallerSeedGrantEvidence> ExpectedInstallerSeedSet(
        TenantId tenant,
        DateTimeOffset at,
        AuthorizationSeedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var expected = new List<InstallerSeedGrantEvidence>
        {
            SeedGrantFor(tenant, at, SchedulerPrincipal, SchedulerRole, SchedulerGrantSource),
            SeedGrantFor(tenant, at, NodeOperatorPrincipal, NodeOperatorRole, NodeOperatorGrantSource),
        };
        if (profile.IncludeDevelopmentGrants)
        {
            expected.Add(SeedGrantFor(tenant, at, DevIndexerPrincipal, DevIndexerRole, DevIndexerGrantSource));
            expected.Add(SeedGrantFor(
                tenant, at, DevWorkflowSeederPrincipal, DevWorkflowSeederRole,
                DevWorkflowSeederGrantSource));
        }
        return expected;
    }

    internal static bool IsExactInstallerSeedSet(
        TenantId tenant,
        IReadOnlyCollection<InstallerSeedGrantEvidence> actual,
        AuthorizationSeedProfile profile)
    {
        if (actual.Count == 0) return false;
        var at = actual.First().Grant.GrantedAt;
        return EqualsExpected(ExpectedInstallerSeedSet(tenant, at, profile), actual);
    }

    private static bool EqualsExpected(
        IReadOnlyCollection<InstallerSeedGrantEvidence> expected,
        IReadOnlyCollection<InstallerSeedGrantEvidence> actual) =>
        expected.Count == actual.Count && expected.All(actual.Contains);

    private static InstallerSeedGrantEvidence SeedGrantFor(
        TenantId tenant,
        DateTimeOffset at,
        string principal,
        RoleReference role,
        string sourceReference)
    {
        var installer = new ActorId("installer:authorization-definition-seed");
        var grant = new AccessGrant(
            GrantIdFor(tenant, sourceReference), tenant, new ActorId(principal), role,
            ScopeExpression.Parse("/"), GrantResidency.Cache, new GrantValidity(at),
            GranterKind.Installer, installer, at,
            new GrantProvenance(
                GrantSourceKind.Bootstrap,
                new GrantReason(GrantReasonCodes.Bootstrap, sourceReference),
                installer),
            at);
        return new InstallerSeedGrantEvidence(grant, grant.Scope.Type, sourceReference);
    }

    private static PlatformBootstrapDecision MintPlatformDefinitionBootstrap(
        AuthorizationDecision foundingAdmission)
    {
        ArgumentNullException.ThrowIfNull(foundingAdmission);
        foundingAdmission.RequireAllowed();
        var request = foundingAdmission.Request;
        var expectedPrincipal = new ActorId("installer:authorization-definition-seed");
        var expectedOperation = AuthorizationOperation.Parse(Permission.GrantPermissions);
        var evidence = foundingAdmission.Resolution;
        if (request.Principal != expectedPrincipal
            || request.Act.Operation != expectedOperation
            || !string.Equals(request.Target.RecordKind, "grant", StringComparison.Ordinal)
            || !Guid.TryParse(request.Target.RecordId, out _)
            || evidence.Count != 1
            || evidence[0].Stage != AuthorizationResolutionStage.Bootstrap
            || !evidence[0].Inputs.SequenceEqual(
                [$"principal:{expectedPrincipal}", $"tenant:{request.Tenant}"], StringComparer.Ordinal)
            || !evidence[0].Outputs.SequenceEqual(
                ["bootstrap:administrator-grant-history-absent"], StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The platform bootstrap mint requires the seed's founding admission.",
                nameof(foundingAdmission));
        }
        return PlatformBootstrapDecision.Mint(foundingAdmission);
    }

    private static AuthorizationCapabilityDefinitionId DefinitionIdFor(AuthorizationOperation operation)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"harborline.authorization-operation/v1:{operation.Value}"));
        return new AuthorizationCapabilityDefinitionId(new Guid(digest.AsSpan(0, 16)));
    }

    private static AuthorizationCapabilityDefinitionId SystemDefinitionIdFor(
        string roleSet,
        AuthorizationOperation operation)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"harborline.system-principal-authorization/v1:{roleSet}:{operation.Value}"));
        return new AuthorizationCapabilityDefinitionId(new Guid(digest.AsSpan(0, 16)));
    }

    internal static GrantId GrantIdFor(TenantId tenant, string sourceReference)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"harborline.system-principal-grant/v1:{tenant.Value}:{sourceReference}"));
        return new GrantId(new Guid(digest.AsSpan(0, 16)));
    }

    private static RoleBindingSet Roles(bool member = false, bool auditor = false, bool nodeOperator = false)
    {
        var roles = new List<RoleReference> { RoleReference.Administrator };
        if (member) roles.Add(MemberRole);
        // `auditor` is true on exactly one row (Permission.AuditRead). AuthorizationDefinitionAdmission
        // refuses a definition that offers Auditor for any other operation, so a second true here does
        // not widen the Auditor -- it fails the seed.
        if (auditor) roles.Add(RoleReference.Auditor);
        if (nodeOperator) roles.Add(NodeOperatorRole);
        return RoleBindingSet.From(roles);
    }
}

internal sealed record InstallerSeedGrantEvidence(
    AccessGrant Grant,
    ScopeExpressionType StoredScopeType,
    string? SourceReference);
