using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Blocks.BuilderDefinitions;

namespace Harborline.Api.LocalNodeHost.Data.Configuration;

/// <summary>
/// The candidate generation's own declared behaviour, read once per run and then replayed into a
/// clean world per case. Nothing here is the effective configuration: every definition is resolved
/// from the exact package version and content digest the candidate names, through the same pack
/// content admission the installer projects with, so what a run examines is the candidate and only
/// the candidate.
/// </summary>
/// <remarks>
/// The candidate carries three kinds of declaration this runner interprets: a form definition (its
/// business rules and its per-section and per-field write roles), a role definition (a name the
/// package ships) and a capability binding (the roles that package offers for one operation). Pack
/// content that declares anything else is not this runner's to interpret and is passed over; it is
/// not silently treated as absent, because an assertion that needed it reads a JSON null and fails.
/// </remarks>
internal sealed class VerificationCandidateWorld
{
    private readonly IReadOnlyDictionary<string, FormDefinition> _forms;
    private readonly IReadOnlyList<RoleDefinition> _roles;
    private readonly IReadOnlyList<AuthorizationCapabilityDefinition> _bindings;
    private readonly TenantId _tenant;

    private VerificationCandidateWorld(TenantId tenant, IReadOnlyDictionary<string, FormDefinition> forms,
        IReadOnlyList<RoleDefinition> roles, IReadOnlyList<AuthorizationCapabilityDefinition> bindings)
    {
        _tenant = tenant;
        _forms = forms;
        _roles = roles;
        _bindings = bindings;
    }

    /// <summary>
    /// Resolves every definition the candidate owns. The candidate states one owning package per
    /// content key and the exact revision of each package, so a content key claimed by two packages
    /// resolves to the one the candidate chose rather than to whichever was read first.
    /// </summary>
    internal static VerificationCandidateWorld? Resolve(DurablePackInstallStore packs, TenantId tenant,
        ConfigurationGeneration candidate, out IReadOnlyList<VerificationRefusal> refusals)
    {
        var found = new List<VerificationRefusal>();
        var references = candidate.References;
        var revisions = references.GetProperty("packages").EnumerateArray().ToDictionary(
            package => package.GetProperty("reference").GetProperty("key").GetString()!,
            package => package.GetProperty("reference").GetProperty("revision").GetString()!,
            StringComparer.Ordinal);
        var forms = new Dictionary<string, FormDefinition>(StringComparer.Ordinal);
        var roles = new List<RoleDefinition>();
        var bindings = new List<AuthorizationCapabilityDefinition>();

        foreach (var owner in references.GetProperty("ownership").EnumerateArray())
        {
            var definitionKey = owner.GetProperty("definitionKey").GetString()!;
            var packageKey = owner.GetProperty("packageKey").GetString()!;
            if (!revisions.TryGetValue(packageKey, out var revision))
            {
                found.Add(new("verification-candidate-package-missing", definitionKey,
                    $"The candidate owns {definitionKey} through {packageKey}, which it does not itself name."));
                continue;
            }
            var pack = packs.GetVersion(tenant, packageKey, revision);
            var item = pack?.SeedItems.FirstOrDefault(seed => seed.Key == definitionKey);
            if (item is null)
            {
                found.Add(new("verification-candidate-content-missing", definitionKey,
                    $"{packageKey}@{revision} does not hold {definitionKey}, so the candidate cannot be executed."));
                continue;
            }
            switch (item.Kind)
            {
                case PackContentKind.FormDefinition:
                    if (TryForm(tenant, packageKey, item, out var form, out var why)) forms[definitionKey] = form!;
                    else found.Add(new("verification-candidate-form-malformed", definitionKey,
                        $"{definitionKey} is not a form definition this host can project: {why}"));
                    break;
                case PackContentKind.RoleDefinition:
                    if (PackAuthorizationContentAdmission.TryParseRoleDefinition(packageKey, item.ParseContent(), out var role))
                        roles.Add(role!);
                    else found.Add(new("verification-candidate-role-malformed", definitionKey,
                        $"{definitionKey} is not a role definition this host can project."));
                    break;
                case PackContentKind.AuthorizationCapabilityBinding:
                    if (PackAuthorizationContentAdmission.TryParseCapabilityBinding(packageKey, item.ParseContent(), out var binding))
                        bindings.Add(binding!);
                    else found.Add(new("verification-candidate-binding-malformed", definitionKey,
                        $"{definitionKey} is not a capability binding this host can project."));
                    break;
                default:
                    break;
            }
        }

        refusals = found;
        return found.Count > 0 ? null : new VerificationCandidateWorld(tenant, forms, roles, bindings);
    }

    /// <summary>
    /// Opens one clean authority world for one fixture. It is in-memory, it is built from the
    /// candidate's own declarations plus the fixture's declared grants and nothing else, and it is
    /// disposed with the case: no grant, role or binding a case installs can reach the next one.
    /// </summary>
    internal async ValueTask<VerificationAuthorityWorld> OpenAsync(VerificationFixture fixture,
        CancellationToken cancellationToken)
    {
        // The world is composed installer-first: the definition writer's OWN authority is a
        // composition detail of building the world, so it is answered by a gate that admits the
        // candidate's declarations and nothing else. No observation is ever taken from it — every
        // decision a case reads comes from the real gate constructed below, over this world's
        // closure. Registering it before the module means the module's TryAdd leaves it in place.
        var installerAuthority = new CandidateInstallerAuthority();
        var services = new ServiceCollection();
        var roster = new VerificationRosterConstraintReader();
        services.AddSingleton<IAuthorizationRosterConstraintReader>(roster);
        services.AddSingleton(new AuthorizationGate(installerAuthority, new EmptyRecordStandingResolver(),
            installerAuthority, roster));
        var provider = services.AddAccessGrantModule().BuildServiceProvider();
        try
        {
            var vocabulary = provider.GetRequiredService<IRoleVocabularyStore>();
            foreach (var role in _roles) await vocabulary.InstallAsync(role, cancellationToken).ConfigureAwait(false);

            // The candidate's capability bindings, through the ordinary admitted write stages: a
            // pack's offered roles are the publisher ceiling and are bounded by every rule
            // AuthorizationDefinitionAdmission holds, exactly as they are at install. The platform
            // seed is deliberately NOT installed — a verification world holds the candidate's
            // authority, not the install's, or a run would pass on authority the candidate never
            // declared.
            var writer = provider.GetRequiredService<AuthorizationDefinitionWriter>();
            var installer = new AuthorizationWriteContext(
                new ActorId("verification:candidate-authority"), _tenant, fixture.Instant);
            foreach (var binding in _bindings)
                await writer.WriteAsync(new InstallAuthorizationDefinition(binding), installer, cancellationToken)
                    .ConfigureAwait(false);

            // The fixture's declared authority, and only it. An actor with an empty grants list is an
            // actor that holds nothing, which is a declared input rather than an oversight.
            var grants = provider.GetRequiredService<IGrantStore>();
            var actor = new ActorId(fixture.Actor);
            var issued = fixture.Instant.AddTicks(-1);
            foreach (var grant in fixture.Grants)
                await grants.AppendAsync(_tenant, new AccessGrant(GrantId.New(), _tenant, actor,
                    Role(grant.RoleKey), ScopeExpression.Parse(Scope(grant.Scope)), GrantResidency.Cache,
                    new GrantValidity(issued), GranterKind.Person, new ActorId("verification:fixture"), issued,
                    new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                        new ActorId("verification:fixture")), issued),
                    // One source reference per declared grant: the store treats a repeated reference as
                    // an attempt to replace immutable evidence, and a fixture may declare two roles.
                    $"{fixture.FixtureId}:{grant.RoleKey}@{grant.Scope}", cancellationToken).ConfigureAwait(false);

            // The REAL gate over this world's closure and definitions — constructed over the same
            // readers the host composes it from, never a stand-in that answers uniformly.
            var gate = new AuthorizationGate(
                provider.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                provider.GetRequiredService<IRecordStandingResolver>(),
                provider.GetRequiredService<IAuthorizationDefinitionAtomReader>(),
                provider.GetRequiredService<IAuthorizationRosterConstraintReader>());
            return new VerificationAuthorityWorld(provider, gate, _tenant);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The submitted fields the candidate's own form definition governs and the roles the gate
    /// derived do not cover, in the candidate's declared field order so a refusal names the same
    /// field on every run. The rule is the released one: a field no section references is
    /// un-governed and writable; a governed field needs both a section whose write roles the actor
    /// holds and, where the field narrows it further, that field's own write roles too.
    /// </summary>
    internal IReadOnlyList<string> WriteDeniedFields(string recordType, JsonObject values,
        IReadOnlySet<RoleReference> held)
    {
        if (Form(recordType) is not { } form) return [];
        var names = held.Select(role => role.Name).Concat(held.Select(role => $"{role.Vocabulary}/{role.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        var sections = form.Overlay.Sections
            .Select(section => (section.Access, Keys: section.Fields.ToHashSet(StringComparer.Ordinal)))
            .ToArray();
        var denied = new List<string>();
        foreach (var property in values)
        {
            var governed = false;
            var writable = false;
            form.Overlay.Fields.TryGetValue(property.Key, out var field);
            foreach (var (access, keys) in sections)
            {
                if (!keys.Contains(property.Key)) continue;
                governed = true;
                if (!Intersects(names, access.WriteRoles)) continue;
                if (field?.FieldWriteRoles is { Count: > 0 } narrowed && !Intersects(names, narrowed)) continue;
                writable = true;
                break;
            }
            if (governed && !writable) denied.Add(property.Key);
        }
        denied.Sort(StringComparer.Ordinal);
        return denied;
    }

    /// <summary>
    /// The record the act produces: the submitted values with every value the candidate's own rules
    /// computed merged over them. A rule set that cannot compile throws rather than degrading, and a
    /// rule that errors or leaves a value pending closes the save gate, which is the released
    /// behaviour — a computed value that failed is not quietly absent.
    /// </summary>
    internal JsonObject Evaluate(string recordType, JsonObject values, VerificationFixture fixture, out bool blocked)
    {
        var record = values.DeepClone().AsObject();
        blocked = false;
        if (Form(recordType) is not { Overlay.Rules.Count: > 0 } form) return record;
        var graph = new FormRuleGraph(RuleCompiler.Compile([.. form.Overlay.Rules]),
            clock: new VerificationClock(fixture.Instant));
        var result = graph.EvaluateInstance(RuleInstance.FromJson(record.DeepClone().AsObject()));
        blocked = result.IsSaveBlocked;
        foreach (var (key, computed) in result.Values)
        {
            if (!key.StartsWith("field:", StringComparison.Ordinal)) continue;
            if (computed.State != ValueState.Resolved) continue;
            record[key["field:".Length..]] = computed.Value?.DeepClone();
        }
        return record;
    }

    /// <summary>The candidate's form definition for a record type, by content key or its last segment.</summary>
    private FormDefinition? Form(string recordType)
    {
        if (_forms.TryGetValue(recordType, out var exact)) return exact;
        return _forms.FirstOrDefault(entry =>
            entry.Key.EndsWith('/' + recordType, StringComparison.Ordinal)).Value;
    }

    private static bool Intersects(IReadOnlySet<string> held, IReadOnlyList<string>? required) =>
        required is { Count: > 0 } && required.Any(held.Contains);

    // A fixture declares a role key in its own terms. A key that already names a vocabulary is taken
    // as written; a bare key is a domain role, which is the only vocabulary a package may ship into.
    private static RoleReference Role(string roleKey)
    {
        var separator = roleKey.IndexOf('/', StringComparison.Ordinal);
        return separator > 0 && separator < roleKey.Length - 1
            ? new(roleKey[..separator], roleKey[(separator + 1)..])
            : new(RoleVocabularies.Domain, roleKey);
    }

    // A fixture's scope is a declared input, so an authored value is used exactly; a bare name is
    // read as one scope segment rather than being widened to the install root.
    private static string Scope(string scope) =>
        string.IsNullOrWhiteSpace(scope) ? "/" : scope[0] == '/' ? scope : "/" + scope;

    private static bool TryForm(TenantId tenant, string packageKey, PackSeedItem item, out FormDefinition? form,
        out string why)
    {
        form = null;
        if (!PackFormDefinitionContent.TryParse(item.CanonicalJson, out var request, out _, out why)) return false;
        try
        {
            var built = FormDefinitionRoutes.BuildDefinition(new FormDefinitionId(item.Key),
                SemanticVersion.Parse(item.Version), tenant, IdentityRef.System,
                new SchemaId($"verification:{packageKey}:{item.Key}"), request.Overlay,
                DateTimeOffset.UnixEpoch, request.CatalogueFieldSource);
            // Pack content has vendor authority: an omitted access gate means NO gate, never the
            // authoring route's operator-role fallback. The installer draws the same distinction, and
            // drawing it differently here would make a verification run disagree with the install.
            form = built with
            {
                Overlay = built.Overlay with
                {
                    Sections = [.. built.Overlay.Sections.Select((section, index) =>
                        request.Overlay.Sections[index].Access is null
                            ? section with { Access = new SectionAccess([], []) }
                            : section)],
                },
            };
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException
            or InvalidOperationException)
        {
            why = exception.Message;
            return false;
        }
    }

    /// <summary>The fixture's virtual clock. Every <c>date.*</c> the candidate's rules read resolves here.</summary>
    private sealed class VerificationClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    /// <summary>
    /// The authority for BUILDING one verification world, and for nothing else. Admitting the
    /// candidate's own declarations into an empty in-memory world is composition, not an act inside
    /// the world: there is no principal in a fresh world who could hold <c>grant:permissions</c>,
    /// and seeding one would put authority into the world that the candidate never declared. So
    /// this answers the writer's own authorize stage and the real gate the case is measured by is a
    /// different object over this world's actual closure — it cannot see this one and never
    /// consults it.
    /// </summary>
    /// <summary>
    /// The roster facts of the declared world. A fixture states its acting persona and the authority
    /// that persona holds, and the gate's fail-closed floor for a composition with no roster is an
    /// EJECTED subject whose atoms are cleared — which would refuse every case for the absence of a
    /// roster the fixture does not have and does not need. So the declared actor is a member of the
    /// declared world and is not ejected. That is the fixture's own input, restated where the gate
    /// reads it; what the actor may DO is still decided entirely by its declared grants and the
    /// candidate's own capability bindings, and an actor with no grants still holds nothing.
    /// </summary>
    private sealed class VerificationRosterConstraintReader : IAuthorizationRosterConstraintReader
    {
        public ValueTask<AuthorizationRosterInputs?> ReadAsync(ActorId principal, TenantId tenant,
            DateTimeOffset at, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AuthorizationRosterInputs?>(
                new(principal.Value, Member: true, Ejected: false) { RegistryMember = true });
    }

    private sealed class CandidateInstallerAuthority
        : IAuthorizationClosureSnapshotReader, IAuthorizationDefinitionAtomReader
    {
        // The act last put to this source. One world is composed on one thread before any case runs,
        // so there is exactly one act in flight; nothing observable is read back from here.
        private PermissionAtom? _requested;

        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(AuthorizationGateRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            _requested = request.Act;
            return ValueTask.FromResult(new AuthorizationClosureSnapshot(
            [
                new AuthorizationAtomDerivation(request.Act, RoleReference.Administrator,
                    "verification:composition", 1, "verification:composition", request.Target.Scope,
                    request.At.AddTicks(-1), null),
            ]));
        }

        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(TenantId tenantId,
            RoleReference role, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<PermissionAtom> atoms =
                role == RoleReference.Administrator && _requested is { } atom ? [atom] : [];
            return ValueTask.FromResult(atoms);
        }
    }
}

/// <summary>One case's clean authority world, disposed with the case.</summary>
internal sealed class VerificationAuthorityWorld(ServiceProvider provider, AuthorizationGate gate, TenantId tenant)
    : IAsyncDisposable
{
    /// <summary>The real gate over this world's closure, definitions and grants.</summary>
    internal AuthorizationGate Gate { get; } = gate;

    /// <summary>The tenant every decision in this world is scoped to.</summary>
    internal TenantId Tenant { get; } = tenant;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
