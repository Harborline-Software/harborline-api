using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Audit.DependencyInjection;
using Harborline.Api.Kernel.Events;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

public sealed class AuthoritySnapshotTests
{
    private static readonly TenantId Tenant = new("tenant-199-audit");
    private static readonly DateTimeOffset At = new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendAuthorized_CopiesAllDistinctGrantPinsFromDecision(bool decorated)
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (decision, _) = await DecisionAsync(signer, Tenant, At,
            ("grant-a", 7), ("grant-b", 11), ("grant-a", 7));
        using var fixture = AuditTrailFixture.Create(decorated, signer);
        var trail = fixture.Authorized;

        await trail.AppendAuthorizedAsync(await RecordAsync(signer, Tenant, At), decision);
        if (decorated) Assert.Equal(1, fixture.InnerAppendCount);

        var stored = Assert.Single(await QueryAsync(trail, Tenant));
        Assert.Equal(
            [new AuthorityGrantSnapshot("grant-a", 7), new AuthorityGrantSnapshot("grant-b", 11)],
            stored.AuthoritySnapshot!.Grants);
        Assert.Equal(["definition"], stored.AuthoritySnapshot.DerivationIds);
        Assert.Equal(signer.IssuerId.ToBase64Url(), stored.AuthoritySnapshot.Principal);
        Assert.Equal(Tenant.Value, stored.AuthoritySnapshot.Tenant);
        Assert.Equal(At, stored.AuthoritySnapshot.Instant);
        Assert.Equal(decision.Resolution.Select(step => step.Stage.ToString()),
            stored.AuthoritySnapshot.Resolution!.Select(step => step.Stage));
        Assert.Equal(decision.Resolution.Select(step => step.Inputs),
            stored.AuthoritySnapshot.Resolution!.Select(step => step.Inputs));
        Assert.Equal(decision.Resolution.Select(step => step.Outputs),
            stored.AuthoritySnapshot.Resolution!.Select(step => step.Outputs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendAuthorized_OverwritesCallerSnapshotAndIsImmutableAfterSourceMutation(bool decorated)
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (decision, source) = await DecisionAsync(signer, Tenant, At, ("decision-grant", 42));
        var mutableDerivations = decision.Derivations.ToList();
        var mutableResolution = decision.Resolution.ToList();
        var mutableResolutionCollections = mutableResolution.Select(step =>
        {
            var inputs = step.Inputs.ToList();
            var outputs = step.Outputs.ToList();
            SetBackingCollection(step, nameof(AuthorizationResolutionStep.Inputs), inputs);
            SetBackingCollection(step, nameof(AuthorizationResolutionStep.Outputs), outputs);
            return (Inputs: inputs, Outputs: outputs);
        }).ToArray();
        SetBackingCollection(decision, nameof(AuthorizationDecision.Derivations), mutableDerivations);
        SetBackingCollection(decision, nameof(AuthorizationDecision.Resolution), mutableResolution);
        var callerGrants = new List<AuthorityGrantSnapshot> { new("caller-forged", 999) };
        var callerSnapshot = Snapshot(callerGrants);
        var record = (await RecordAsync(signer, Tenant, At)) with { AuthoritySnapshot = callerSnapshot };
        using var fixture = AuditTrailFixture.Create(decorated, signer);
        var trail = fixture.Authorized;

        await trail.AppendAuthorizedAsync(record, decision);
        var expectedGrants = decision.Derivations
            .Select(item => new AuthorityGrantSnapshot(item.GrantId, item.GrantOwnerVersion))
            .Distinct().ToArray();
        var expectedDerivations = decision.Derivations.Select(item => item.DefinitionId).Distinct().ToArray();
        var expectedResolution = decision.Resolution.Select(step => (
            Stage: step.Stage.ToString(),
            Inputs: step.Inputs.ToArray(),
            Outputs: step.Outputs.ToArray())).ToArray();
        source.Pins.Clear();
        source.Pins.Add(("later-grant-row", 1000));
        callerGrants[0] = new AuthorityGrantSnapshot("caller-mutated", 1001);
        mutableDerivations.Clear();
        foreach (var (inputs, outputs) in mutableResolutionCollections)
        {
            inputs.Clear();
            inputs.Add("later-input");
            outputs.Clear();
            outputs.Add("later-output");
        }
        mutableResolution.Clear();

        var stored = Assert.Single(await QueryAsync(trail, Tenant));
        Assert.NotSame(callerSnapshot, stored.AuthoritySnapshot);
        var snapshot = stored.AuthoritySnapshot!;
        Assert.Equal(expectedGrants.Length, snapshot.Grants.Count);
        for (var index = 0; index < expectedGrants.Length; index++)
            Assert.Equal(expectedGrants[index], snapshot.Grants[index]);
        Assert.Equal(expectedDerivations.Length, snapshot.DerivationIds!.Count);
        for (var index = 0; index < expectedDerivations.Length; index++)
            Assert.Equal(expectedDerivations[index], snapshot.DerivationIds[index]);
        Assert.NotNull(snapshot.Resolution);
        Assert.Equal(expectedResolution.Length, snapshot.Resolution!.Count);
        Assert.NotSame(mutableResolution, snapshot.Resolution);
        for (var index = 0; index < expectedResolution.Length; index++)
        {
            var actual = snapshot.Resolution[index];
            Assert.Equal(expectedResolution[index].Stage, actual.Stage);
            Assert.Equal(expectedResolution[index].Inputs.Length, actual.Inputs.Count);
            Assert.Equal(expectedResolution[index].Outputs.Length, actual.Outputs.Count);
            Assert.Equal(expectedResolution[index].Inputs, actual.Inputs);
            Assert.Equal(expectedResolution[index].Outputs, actual.Outputs);
            Assert.NotSame(mutableResolutionCollections[index].Inputs, actual.Inputs);
            Assert.NotSame(mutableResolutionCollections[index].Outputs, actual.Outputs);
        }
    }

    private static void SetBackingCollection<T>(object owner, string property, T value)
    {
        var field = owner.GetType().GetField(
            $"<{property}>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing backing field for {owner.GetType().Name}.{property}.");
        field.SetValue(owner, value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendAuthorized_RejectsDeniedOrTenantPrincipalInstantMismatch(bool decorated)
    {
        using var keys = KeyPair.Generate();
        using var otherKeys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var otherSigner = new Ed25519Signer(otherKeys);
        var (allowed, _) = await DecisionAsync(signer, Tenant, At, ("grant", 1));
        var (denied, _) = await DecisionAsync(signer, Tenant, At);
        using var fixture = AuditTrailFixture.Create(decorated, signer);
        var trail = fixture.Authorized;

        await AssertRefusedAsync(trail, await RecordAsync(signer, Tenant, At), denied,
            AuthorizedAuditRefusalCodes.DecisionDenied);
        await AssertRefusedAsync(trail, await RecordAsync(signer, new TenantId("other-tenant"), At), allowed,
            AuthorizedAuditRefusalCodes.TenantMismatch);
        await AssertRefusedAsync(trail, await RecordAsync(otherSigner, Tenant, At), allowed,
            AuthorizedAuditRefusalCodes.PrincipalMismatch);
        await AssertRefusedAsync(trail, await RecordAsync(signer, Tenant, At.AddTicks(1)), allowed,
            AuthorizedAuditRefusalCodes.InstantMismatch);
        await AssertRefusedAsync(trail, await RecordAsync(signer, Tenant, At, recordId: "record-b"), allowed,
            AuthorizedAuditRefusalCodes.TargetMismatch);
        await AssertRefusedAsync(trail, await RecordAsync(signer, Tenant, At, recordKind: "invoice"), allowed,
            AuthorizedAuditRefusalCodes.TargetMismatch);
        await AssertRefusedAsync(trail, await RecordAsync(signer, Tenant, At, operation: "records:delete"), allowed,
            AuthorizedAuditRefusalCodes.ActMismatch);
        Assert.Empty(await QueryAsync(trail, Tenant));
    }

    [Fact]
    public async Task FormMintAndPackAudit_CorrespondenceComesFromAct_NotDecision()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var actor = new ActorId(signer.IssuerId.ToBase64Url());
        var token = (CapabilityToken)Activator.CreateInstance(
            typeof(CapabilityToken),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [Tenant, actor, Array.Empty<string>(), new[] { FormCapabilityAction.Write }, At.AddHours(1)],
            culture: null)!;
        var submittedForm = new FormDefinitionId("form-a");
        var otherFormDecision = TestAuthorization.AllowedDecision(
            Tenant, "form-b", "forms", Permission.FormsAuthor, actor.Value, At);
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var signedFormPayload = (await RecordAsync(signer, Tenant, At)).Payload;
        var formTrail = new InMemoryAuditTrail();
        var appendMint = typeof(FormEngine).GetMethod("AppendMintAuditAsync", flags)!;
        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() => appendMint.Invoke(null,
            [formTrail, signedFormPayload, submittedForm, token, At, otherFormDecision, CancellationToken.None]));
        var formRefusal = Assert.IsType<AuthorizedAuditRefusedException>(invocation.InnerException);
        Assert.Equal(AuthorizedAuditRefusalCodes.TargetMismatch, formRefusal.Code);
        Assert.Empty(await QueryAsync(formTrail, Tenant));

        using var nodeSigner = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var packTrail = new InMemoryAuditTrail();
        var adapter = new KernelAuditPackInstallAudit(
            packTrail, nodeSigner, NullLogger<KernelAuditPackInstallAudit>.Instance);
        var entry = new PackInstallAuditEntry(
            Tenant, PackInstallAuditAction.Activated, "pack-a", "1.0.0", At,
            null, null, "activated", ActingPrincipal: "test-operator");
        var otherPackDecision = TestAuthorization.AllowedDecision(
            Tenant, "pack-b", "pack", Permission.PackagesOperate, "test-operator", At);
        adapter.AppendAuthorized(entry, otherPackDecision);
        Assert.Empty(await QueryAsync(packTrail, Tenant));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendAuthorized_DoesNotReadAmbientAttributionOrCurrentGrantRows(bool decorated)
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (decision, source) = await DecisionAsync(signer, Tenant, At, ("carried-grant", 5));
        using var fixture = AuditTrailFixture.Create(decorated, signer);
        var trail = fixture.Authorized;

        var readsBeforeAppend = source.ReadCalls;
        source.ThrowOnRead = true;
        await trail.AppendAuthorizedAsync(await RecordAsync(signer, Tenant, At), decision);

        Assert.Equal(readsBeforeAppend, source.ReadCalls);
        var pin = Assert.Single(Assert.Single(await QueryAsync(trail, Tenant)).AuthoritySnapshot!.Grants);
        Assert.Equal(new AuthorityGrantSnapshot("carried-grant", 5), pin);
        AssertNoForbiddenAuthorityReconstructionDependencies(AuditAppendSymbolInventory.Discover());
    }

    [Fact]
    public async Task ShippingComposedAuditReadPath_NeverInvokesAuthorizationOrAuthoritySources()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var forbidden = new ThrowingAuthorizationSources();
        using var provider = new ServiceCollection()
            .AddSingleton<IEventLog, InMemoryEventLog>()
            .AddSingleton<IOperationVerifier, Ed25519Verifier>()
            .AddSingleton<IOperationSigner>(signer)
            .AddSingleton<IAuthorizationClosureSnapshotReader>(forbidden)
            .AddSingleton<IAuthorizationDefinitionAtomReader>(forbidden)
            .AddSingleton<IRecordStandingResolver>(forbidden)
            .AddEnrollmentCompensatingControlAudit()
            .BuildServiceProvider();
        LocalNodeFinalGraphServiceProviderFactory.EnsureShippingAuditIdentity(provider);
        var trail = provider.GetRequiredService<IAuditTrail>();
        await trail.AppendAsync(await RecordAsync(signer, Tenant, At));

        var reader = provider.GetRequiredService<IAuditEventReader>();
        Assert.Single((await reader.ListAsync(Tenant, new AuditEventReaderQuery())).Records);
        Assert.Equal(0, forbidden.Calls);
        Assert.DoesNotContain(typeof(IAuditTrail).Assembly.GetTypes(),
            type => type.Name.Contains("AuthoritySnapshotSource", StringComparison.Ordinal));
        AssertNoForbiddenAuthorityReconstructionDependencies(AuditAppendSymbolInventory.Discover());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryAppend_StoresNullAuthoritySnapshot(bool decorated)
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        using var fixture = AuditTrailFixture.Create(decorated, signer);
        var trail = fixture.Trail;
        var record = (await RecordAsync(signer, Tenant, At)) with
        {
            AuthoritySnapshot = Snapshot([new AuthorityGrantSnapshot("caller-forged", 99)]),
        };

        await trail.AppendAsync(record);

        Assert.Null(Assert.Single(await QueryAsync(trail, Tenant)).AuthoritySnapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryAuditedKernelWrite_CarriesTheSameDecisionThatPermittedMutation(bool decorated)
    {
        using var fixtureKeys = KeyPair.Generate();
        var fixtureSigner = new Ed25519Signer(fixtureKeys);
        using var auditFixture = AuditTrailFixture.Create(decorated, fixtureSigner);
        Assert.Same(auditFixture.Trail, auditFixture.Authorized);
        using var kernelServices = new ServiceCollection()
            .AddSingleton<IEventLog, InMemoryEventLog>()
            .AddSingleton<IOperationVerifier, Ed25519Verifier>()
            .AddHarborlineKernelAudit()
            .BuildServiceProvider();
        Assert.Same(kernelServices.GetRequiredService<IAuditTrail>(),
            kernelServices.GetRequiredService<IAuthorizedAuditTrail>());

        using var sodServices = new ServiceCollection()
            .AddSingleton<IOperationSigner>(fixtureSigner)
            .AddEnrollmentCompensatingControlAudit()
            .BuildServiceProvider();
        Assert.Same(sodServices.GetRequiredService<IAuditTrail>(),
            sodServices.GetRequiredService<IAuthorizedAuditTrail>());
        var composedReader = Assert.IsType<InMemoryAuditEventReader>(
            sodServices.GetRequiredService<IAuditEventReader>());
        var readerTrail = typeof(InMemoryAuditEventReader)
            .GetField("_trail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composedReader);
        Assert.Same(sodServices.GetRequiredService<IAuditTrail>(), readerTrail);

        var recorded = new RecordingPackInstallAudit();
        var gateCalls = 0;
        var store = new InMemoryPackInstallStore();
        var codec = new PackFileCodec();
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            store,
            new WorkflowRefusingPackContentAdmission(),
            recorded,
            TestAuthorization.Gate(true, _ => gateCalls++));
        var trust = new InMemoryPackTrustStore(
            [new PackTrustRoot(TrustScope.OwnRoster, fixtureKeys.PrincipalId, 1, TrustRootStatus.Current)]);
        var context = new PackInstallContext(
            Tenant,
            trust,
            PackRevocationList.Empty,
            At,
            TimeSpan.FromDays(30),
            Principal: "test-operator");

        var refused = installer.Install(Array.Empty<byte>(), context);
        Assert.Same(refused.Decision, recorded.LastDecision);

        var v2 = await CreatePackAsync(codec, fixtureSigner, "2.0.0");
        var installed = installer.Install(v2, context);
        Assert.True(installed.Installed);
        Assert.Same(installed.Decision, recorded.LastDecision);

        var activated = installer.Activate(context, installed.PackKey, installed.Version);
        Assert.True(activated.Activated);
        Assert.Same(activated.Decision, recorded.LastDecision);
        var narrowingDecision = TestAuthorization.AllowedDecision(
            Tenant, installed.PackKey, "pack", Permission.PackagesOperate, at: At);
        var beforeNarrowing = recorded.Decisions.Count;
        var beforeNarrowingGateCalls = gateCalls;
        var narrowed = installer.Narrow(context, installed.PackKey, "ticket-199-form",
            new JsonObject { ["title"] = null }, narrowingDecision);
        Assert.True(narrowed.Recorded);
        Assert.Same(narrowingDecision, narrowed.Decision);
        Assert.Same(narrowingDecision, recorded.LastDecision);
        var unknown = installer.Narrow(context, installed.PackKey, "missing-content",
            new JsonObject(), narrowingDecision);
        Assert.False(unknown.Recorded);
        Assert.Same(narrowingDecision, unknown.Decision);
        var widened = installer.Narrow(context, installed.PackKey, "ticket-199-form",
            new JsonObject { ["title"] = "widened" }, narrowingDecision);
        Assert.False(widened.Recorded);
        Assert.Same(narrowingDecision, widened.Decision);
        Assert.Equal(beforeNarrowing + 3, recorded.Decisions.Count);
        Assert.All(recorded.Decisions.Skip(beforeNarrowing), item => Assert.Same(narrowingDecision, item));
        Assert.Equal(beforeNarrowingGateCalls, gateCalls);

        var deactivated = installer.Deactivate(context, installed.PackKey, installed.Version);
        Assert.True(deactivated.Deactivated);
        Assert.Same(deactivated.Decision, recorded.LastDecision);
        var inactive = installer.Narrow(context, installed.PackKey, "ticket-199-form",
            new JsonObject { ["title"] = null }, narrowingDecision);
        Assert.False(inactive.Recorded);
        Assert.Same(narrowingDecision, inactive.Decision);
        Assert.Same(narrowingDecision, recorded.LastDecision);

        var v1 = await CreatePackAsync(codec, fixtureSigner, "1.0.0");
        var downgradeRefusal = installer.Install(v1, context);
        Assert.False(downgradeRefusal.Installed);
        Assert.Same(downgradeRefusal.Decision, recorded.LastDecision);
        var beforeBreakGlass = recorded.Decisions.Count;
        var brokeGlass = installer.Install(v1, context with
        {
            BreakGlass = new BreakGlass("ticket-199 mutation exercise", "test-operator"),
        });
        Assert.True(brokeGlass.Installed);
        Assert.All(recorded.Decisions.Skip(beforeBreakGlass), item => Assert.Same(brokeGlass.Decision, item));

        var emittedAuditCalls = AuditAppendSymbolInventory.Discover();
        Assert.NotEmpty(emittedAuditCalls);
        Assert.Empty(FindDecisionIdentityOffenders(emittedAuditCalls.Where(site =>
            site.Kind is AuditAppendKind.Authorized or AuditAppendKind.PackAuthorized)));
        AssertAuthorizedAppendInventory(emittedAuditCalls);
        AssertNoForbiddenAuthorityReconstructionDependencies(emittedAuditCalls);
        AssertTicket205OrdinaryDomainAppendInventory(emittedAuditCalls);

        var plantedDecisionOffenders = new[]
        {
            ("packages/planted/SingleRedecisionWriter.cs",
             "async Task Write(IAuthorizedAuditTrail audit, AuthorizationDecision admittedDecision) { var local = await gate.DecideAsync(request); await audit.AppendAuthorizedAsync(record, admittedDecision); }"),
            ("packages/planted/CloneWriter.cs",
             "async Task Write(IAuthorizedAuditTrail audit, AuthorizationDecision admittedDecision) { var clone = Clone(admittedDecision); await audit.AppendAuthorizedAsync(record, clone); }"),
            ("packages/planted/LocalAliasWriter.cs",
             "async Task Write(IAuthorizedAuditTrail audit, AuthorizationDecision admittedDecision) { var local = admittedDecision; await audit.AppendAuthorizedAsync(record, local); }"),
        };
        Assert.Equal(plantedDecisionOffenders.Select(item => item.Item1).Order(StringComparer.Ordinal),
            FindDecisionIdentityOffenders(plantedDecisionOffenders));

        var plantedAppend = new AuditAppendCallSite(
            AuditAppendKind.Ordinary,
            "packages/planted/NewUngatedWriter.cs",
            "Harborline.Planted.NewUngatedWriter.WriteAsync",
            17,
            "Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync",
            0,
            "WriteAsync");
        Assert.ThrowsAny<Exception>(() =>
            AssertTicket205OrdinaryDomainAppendInventory(emittedAuditCalls.Append(plantedAppend)));
    }

    [Fact]
    public void AuditInventoryKeyExcludesPdbLine()
    {
        var original = new AuditAppendCallSite(
            AuditAppendKind.Authorized,
            "packages/example/Writer.cs",
            "Harborline.Example.Writer.WriteAsync",
            40,
            "Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail.AppendAuthorizedAsync",
            0,
            "WriteAsync");

        Assert.Equal(SiteKey(original), SiteKey(original with { Line = original.Line + 20 }));
    }

    private static async Task<byte[]> CreatePackAsync(
        PackFileCodec codec,
        IOperationSigner signer,
        string version)
    {
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec,
            TimeProvider.System);
        var export = await exporter.ExportAsync(new PackExportRequest(
            "ticket.199.audit", version, "Ticket 199 audit", "Decision identity runtime path",
            PackScopeTier.Horizontal,
            [new PackContentSource("ticket-199-form", PackContentKind.FormDefinition, version,
                new JsonObject { ["title"] = "ticket 199" })],
            Array.Empty<PackDependencyRef>(),
            Array.Empty<string>(),
            1,
            Dcp: DomainComplianceProfile.General("ticket-199")), signer);
        Assert.True(export.Succeeded);
        return export.FileBytes!;
    }

    private static async Task AssertRefusedAsync(
        IAuthorizedAuditTrail trail,
        AuditRecord record,
        AuthorizationDecision decision,
        string code)
    {
        var refusal = await Assert.ThrowsAsync<AuthorizedAuditRefusedException>(async () =>
            await trail.AppendAuthorizedAsync(record, decision));
        Assert.Equal(code, refusal.Code);
    }

    internal static async Task<(AuthorizationDecision Decision, MutableDecisionSource Source)> DecisionAsync(
        IOperationSigner signer,
        TenantId tenant,
        DateTimeOffset at,
        params (string GrantId, long OwnerVersion)[] pins)
    {
        var source = new MutableDecisionSource(pins);
        var gate = new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
        var scope = ScopeExpression.Parse("/records/audit-record");
        var request = new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse("records:write"), scope),
            new ActorId(signer.IssuerId.ToBase64Url()),
            tenant,
            new AuthorizationTarget("record", "audit-record", scope),
            at);
        return (await gate.DecideAsync(request), source);
    }

    internal static async Task<AuditRecord> RecordAsync(
        IOperationSigner signer,
        TenantId tenant,
        DateTimeOffset at,
        string recordKind = "record",
        string recordId = "audit-record",
        string operation = "records:write")
    {
        var scope = ScopeExpression.Parse("/records/audit-record");
        var act = new PermissionAtom(AuthorizationOperation.Parse(operation), scope);
        var payload = await signer.SignAsync(
            new AuditPayload(new Dictionary<string, object?> { ["act"] = "write" }), at, Guid.NewGuid());
        return new AuditRecord(
            Guid.NewGuid(), tenant, AuditEventType.PaymentAuthorized,
            at, payload, Array.Empty<AttestingSignature>(),
            Actor: new ActorId(signer.IssuerId.ToBase64Url()),
            Target: new AuthorizationTarget(recordKind, recordId, scope),
            Act: act);
    }

    private static async Task<IReadOnlyList<AuditRecord>> QueryAsync(IAuditTrail trail, TenantId tenant)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in trail.QueryAsync(new AuditQuery(tenant))) records.Add(record);
        return records;
    }

    private static AuthoritySnapshot Snapshot(IReadOnlyList<AuthorityGrantSnapshot> grants) => new(
        grants, Policy: null, AppliedLimit: null, LimitSource: null,
        SeparationOfDuty: null, PostingPeriodState: null);

    private static void AssertTicket205OrdinaryDomainAppendInventory(
        IEnumerable<AuditAppendCallSite> discovered)
    {
        // Ticket 205 deliberately preserves these ordinary append gates. Each allowance is an
        // emitted call site pinned to file + containing symbol + target + IL ordinal; no receiver spelling or
        // hand-picked file scan can conceal a new ordinary append.
        string[] allowed =
        {
            "apps/local-node-host/Enrollment/KernelAuditEnrollmentCompensatingControlRecorder.cs|Harborline.Api.LocalNodeHost.Enrollment.KernelAuditEnrollmentCompensatingControlRecorder.EmitAsync(Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Kernel.Audit.AuditEventType,System.Collections.Generic.IReadOnlyDictionary`2[System.String,System.Object],System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            // Ticket 214 slice 2 — the refusal audit row. ORDINARY by construction: the decision it
            // records is DENIED, and AppendAuthorizedAsync refuses a denied decision
            // (AuthorizedAuditRefusalCodes.DecisionDenied). It carries the very decision the guard made
            // (or preDecision=true when the act never reached one) and re-decides nothing.
            "apps/local-node-host/Health/AuthorizationRefusalAudit.cs|Harborline.Api.LocalNodeHost.Health.AuthorizationRefusalAudit.RecordCoreAsync(Harborline.Api.Foundation.Authorization.AuthorizationRefusal,System.String,Harborline.Api.Foundation.Assets.Common.ActorId,Harborline.Api.Foundation.Assets.Common.TenantId,System.DateTimeOffset,Harborline.Api.Foundation.Authorization.AuthorizationDecision,Harborline.Api.Kernel.Audit.AuditEventType,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "apps/local-node-host/Health/KernelAuditPackInstallAudit.cs|Harborline.Api.LocalNodeHost.Health.KernelAuditPackInstallAudit.AppendCore(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/blocks-financial-ap/Services/InMemoryBillRepository.cs|Harborline.Api.Blocks.FinancialAp.Services.InMemoryBillRepository.EmitTenantBoundaryViolationAsync(System.String,Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Assets.Common.TenantId,System.Nullable`1[System.DateTimeOffset],System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/blocks-financial-ar/Services/InMemoryInvoiceRepository.cs|Harborline.Api.Blocks.FinancialAr.Services.InMemoryInvoiceRepository.EmitTenantBoundaryViolationAsync(System.String,Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Assets.Common.TenantId,System.Nullable`1[System.DateTimeOffset],System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/blocks-financial-ledger/Services/IJournalStore.cs|Harborline.Api.Blocks.FinancialLedger.Services.InMemoryJournalStore.EmitTenantBoundaryViolationAsync(System.String,Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/blocks-financial-payments/Services/InMemoryPaymentApplicationRepository.cs|Harborline.Api.Blocks.FinancialPayments.Services.InMemoryPaymentApplicationRepository.EmitTenantBoundaryViolationAsync(System.String,Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Assets.Common.TenantId,System.Nullable`1[System.DateTimeOffset],System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/blocks-financial-payments/Services/InMemoryPaymentRepository.cs|Harborline.Api.Blocks.FinancialPayments.Services.InMemoryPaymentRepository.EmitTenantBoundaryViolationAsync(System.String,Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Assets.Common.TenantId,System.Nullable`1[System.DateTimeOffset],System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/blocks-leases/Services/InMemoryLeaseService.cs|Harborline.Api.Blocks.Leases.Services.InMemoryLeaseService.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/blocks-maintenance/Services/InMemoryMaintenanceService.cs|Harborline.Api.Blocks.Maintenance.Services.InMemoryMaintenanceService.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-catalog/ExtensionFields/ExtensionFieldCatalog.cs|Harborline.Api.Foundation.Catalog.ExtensionFields.ExtensionFieldCatalog.EmitAuditAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Foundation.FeatureManagement.FeatureEvaluationContext,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-migration/Services/InMemoryFormFactorMigrationService.cs|Harborline.Api.Foundation.Migration.InMemoryFormFactorMigrationService.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-mission-space-regulatory/Audit/RegulatoryAuditEmitter.cs|Harborline.Api.Foundation.MissionSpace.Regulatory.Audit.RegulatoryAuditEmitter.EmitInternalAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-mission-space/Services/DefaultFeatureForceEnableSurface.cs|Harborline.Api.Foundation.MissionSpace.DefaultFeatureForceEnableSurface.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-mission-space/Services/DefaultInstallForceEnableSurface.cs|Harborline.Api.Foundation.MissionSpace.DefaultInstallForceEnableSurface.EmitAsync(Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-mission-space/Services/DefaultMinimumSpecResolver.cs|Harborline.Api.Foundation.MissionSpace.DefaultMinimumSpecResolver.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-mission-space/Services/DefaultMissionEnvelopeProvider.cs|Harborline.Api.Foundation.MissionSpace.DefaultMissionEnvelopeProvider.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.AuditPreDecisionRefusal(Harborline.Api.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,System.String): System.Void|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.Append(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry): System.Void|0",
            "packages/foundation-recovery/Crypto/SubjectKeyFieldDecryptor.cs|Harborline.Api.Foundation.Recovery.Crypto.SubjectKeyFieldDecryptor.EmitAuditAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,Harborline.Api.Foundation.Assets.Common.TenantId,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-recovery/Crypto/TenantKeyProviderFieldDecryptor.cs|Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldDecryptor.EmitAuditAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,Harborline.Api.Foundation.Assets.Common.TenantId,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-recovery/Erasure/SubjectErasureService.cs|Harborline.Api.Foundation.Recovery.Erasure.SubjectErasureService.EmitErasedAsync(Harborline.Api.Foundation.Recovery.Erasure.SubjectTombstone,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-recovery/Erasure/SubjectErasureService.cs|Harborline.Api.Foundation.Recovery.Erasure.SubjectErasureService.EmitShredBlockedAsync(Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Recovery.Erasure.SubjectId,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-recovery/LegalHold/LegalHoldService.cs|Harborline.Api.Foundation.Recovery.LegalHold.LegalHoldService.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,Harborline.Api.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-security-policy/Issuance/DefaultSecurityPolicyIssuer.cs|Harborline.Api.Foundation.SecurityPolicy.Issuance.DefaultSecurityPolicyIssuer.EmitSecurityPolicyAuditAsync``1[!!0](Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Foundation.Assets.Common.TenantId,!!0,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-ship-common/DefaultPermissionResolver.cs|Harborline.Api.Foundation.Ship.Common.DefaultPermissionResolver.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-taxonomy/Services/InMemoryTaxonomyRegistry.cs|Harborline.Api.Foundation.Taxonomy.Services.InMemoryTaxonomyRegistry.EmitAsync(Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-transport/Selection/DefaultTransportSelector.cs|Harborline.Api.Foundation.Transport.DefaultTransportSelector.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-versioning/Services/InMemoryVersionVectorIncompatibility.cs|Harborline.Api.Foundation.Versioning.InMemoryVersionVectorIncompatibility.EmitAuditAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-wayfinder/DefaultOodWatchService.cs|Harborline.Api.Foundation.Wayfinder.DefaultOodWatchService.EmitAuditAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Wayfinder.OodWatchId,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-wayfinder/DefaultStandingOrderIssuer.cs|Harborline.Api.Foundation.Wayfinder.DefaultStandingOrderIssuer.EmitAuditAsync(Harborline.Api.Kernel.Audit.IAuditTrail,Harborline.Api.Kernel.Audit.AuditEventType,System.Guid,Harborline.Api.Foundation.Wayfinder.StandingOrder,Harborline.Api.Foundation.Wayfinder.StandingOrderValidationResult,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-wayfinder/DefaultStandingOrderIssuer.cs|Harborline.Api.Foundation.Wayfinder.DefaultStandingOrderIssuer.EmitRescindAuditAsync(Harborline.Api.Kernel.Audit.IAuditTrail,System.Guid,Harborline.Api.Foundation.Wayfinder.StandingOrder,Harborline.Api.Foundation.Assets.Common.ActorId,System.String,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-wayfinder/OodWatchExpiryService.cs|Harborline.Api.Foundation.Wayfinder.OodWatchExpiryService.EmitExpiredAuditAsync(Harborline.Api.Foundation.Wayfinder.OodWatch,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/kernel-audit/Export/AuditExportService.cs|Harborline.Api.Kernel.Audit.Export.AuditExportService.ExportAsync(Harborline.Api.Foundation.Assets.Common.TenantId,System.String,System.Nullable`1[System.DateTimeOffset],System.Nullable`1[System.DateTimeOffset],System.String,System.Threading.CancellationToken): System.Threading.Tasks.Task`1[System.Int32]|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/kernel-audit/InMemoryAuditEventReader.cs|Harborline.Api.Kernel.Audit.InMemoryAuditEventReader.EmitTenantBoundaryViolationAsync(System.String,Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Assets.Common.TenantId,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
            "packages/kernel-signatures/Services/SignatureAuditEmitter.cs|Harborline.Api.Kernel.Signatures.Services.SignatureAuditEmitter.EmitAsync(Harborline.Api.Kernel.Audit.AuditEventType,Harborline.Api.Kernel.Audit.AuditPayload,System.DateTimeOffset,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuditTrail.AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|0",
};

        var actual = discovered
            .Where(site => site.Kind is AuditAppendKind.Ordinary or AuditAppendKind.PackOrdinary)
            .Select(SiteKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = allowed.Order(StringComparer.Ordinal).ToArray();
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            string.Join(Environment.NewLine, discovered
                .Where(site => site.Kind is AuditAppendKind.Ordinary or AuditAppendKind.PackOrdinary)
                .Select(DescribeSite)));
    }

    private static string SiteKey(AuditAppendCallSite site) =>
        $"{site.File}|{site.Symbol}|{site.CalledMethod}|{site.Ordinal}";

    private static string DescribeSite(AuditAppendCallSite site) =>
        $"{SiteKey(site)} @ {site.File}:{site.Line}";

    private static void AssertAuthorizedAppendInventory(IEnumerable<AuditAppendCallSite> discovered)
    {
        string[] allowed =
        {
            // Ticket 208 slice 4: Post-admission narrowing refusals carry the same caller guard decision; no second decision.
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.AuditNarrowingRefusal(Harborline.Api.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,System.String,System.String,Harborline.Api.Foundation.Authorization.AuthorizationDecision): Harborline.Api.Foundation.Packs.Install.PackNarrowingOutcome|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|0",
            // Ticket 208 slice 4: Admitted narrowing persists the ordinary override and audits with the caller guard decision.
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.Narrow(Harborline.Api.Foundation.Packs.Install.PackInstallContext,System.String,System.String,System.Text.Json.Nodes.JsonNode,Harborline.Api.Foundation.Authorization.AuthorizationDecision): Harborline.Api.Foundation.Packs.Install.PackNarrowingOutcome|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|0",
            "apps/local-node-host/Health/KernelAuditPackInstallAudit.cs|Harborline.Api.LocalNodeHost.Health.KernelAuditPackInstallAudit.AppendCore(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail.AppendAuthorizedAsync(Harborline.Api.Kernel.Audit.AuditRecord,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken,Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-forms-engine/FormEngine.cs|Harborline.Api.Foundation.Forms.Engine.FormEngine.AppendMintAuditAsync(Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail,Harborline.Api.Foundation.Crypto.SignedOperation`1[Harborline.Api.Kernel.Audit.AuditPayload],Harborline.Api.Foundation.Forms.Models.FormDefinitionId,Harborline.Api.Foundation.Forms.Engine.Capabilities.CapabilityToken,System.DateTimeOffset,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail.AppendAuthorizedAsync(Harborline.Api.Kernel.Audit.AuditRecord,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken,Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision): System.Threading.Tasks.ValueTask|0",
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.Install(System.ReadOnlySpan`1[System.Byte],Harborline.Api.Foundation.Packs.Install.PackInstallContext): Harborline.Api.Foundation.Packs.Install.PackInstallOutcome|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|0",
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.Install(System.ReadOnlySpan`1[System.Byte],Harborline.Api.Foundation.Packs.Install.PackInstallContext): Harborline.Api.Foundation.Packs.Install.PackInstallOutcome|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|1",
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.ActivateCore(Harborline.Api.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,System.Collections.Generic.IReadOnlyDictionary`2[System.String,System.String],Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority&): Harborline.Api.Foundation.Packs.Install.PackActivationOutcome|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|0",
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.DeactivateCore(Harborline.Api.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,Harborline.Api.Foundation.Packs.Install.PackProjectionAuthority&): Harborline.Api.Foundation.Packs.Install.PackDeactivationOutcome|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|0",
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.AuditRefused(Harborline.Api.Foundation.Packs.Install.PackInstallContext,Harborline.Api.Foundation.Packs.Install.PackInstallPreview,System.Nullable`1[Harborline.Api.Foundation.Crypto.PrincipalId],Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|0",
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.AuditActivationRefusal(Harborline.Api.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,System.String,System.String,Harborline.Api.Foundation.Authorization.AuthorizationDecision): Harborline.Api.Foundation.Packs.Install.PackActivationOutcome|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|0",
            "packages/foundation-packs/Install/PackInstaller.cs|Harborline.Api.Foundation.Packs.Install.PackInstaller.AuditDeactivationRefusal(Harborline.Api.Foundation.Assets.Common.TenantId,System.String,System.String,System.DateTimeOffset,System.String,System.String,Harborline.Api.Foundation.Authorization.AuthorizationDecision): Harborline.Api.Foundation.Packs.Install.PackDeactivationOutcome|Harborline.Api.Foundation.Packs.Install.Audit.IPackInstallAudit.AppendAuthorized(Harborline.Api.Foundation.Packs.Install.Audit.PackInstallAuditEntry,Harborline.Api.Foundation.Authorization.AuthorizationDecision): System.Void|0",
                            "apps/local-node-host/CompromisedDeviceResponse/NodeRosterCompromisedDeviceRevocationPublisher.cs|Harborline.Api.LocalNodeHost.CompromisedDeviceResponse.NodeRosterMemberRevocationAuthority.RevokeAsync(Harborline.Api.Foundation.Assets.Common.TenantId,System.String,System.String,System.String,System.String,System.String,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken): System.Threading.Tasks.ValueTask`1[Harborline.Api.LocalNodeHost.CompromisedDeviceResponse.CompromisedDeviceRevocation]|Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail.AppendAuthorizedAsync(Harborline.Api.Kernel.Audit.AuditRecord,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken,Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision): System.Threading.Tasks.ValueTask|0",
            "apps/local-node-host/CompromisedDeviceResponse/NodeRosterCompromisedDeviceRevocationPublisher.cs|Harborline.Api.LocalNodeHost.CompromisedDeviceResponse.NodeRosterMemberRevocationAuthority.AppendRefusalAuditAsync(Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Foundation.Authorization.AuthorizationGateRequest,System.String,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail.AppendAuthorizedAsync(Harborline.Api.Kernel.Audit.AuditRecord,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken,Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision): System.Threading.Tasks.ValueTask|0",
            "apps/local-node-host/Data/Identity/AdminTeamAccessAuthority.cs|Harborline.Api.LocalNodeHost.Data.Identity.AdminTeamAccessAuthority.AppendGrantAuditAsync(Harborline.Api.Foundation.Assets.Common.TenantId,Harborline.Api.Blocks.AccessGrant.GrantId,Harborline.Api.Foundation.Authorization.AuthorizationDecision,Harborline.Api.Kernel.Audit.AuditEventType,System.String,System.Guid,System.Nullable`1[Harborline.Api.Blocks.AccessGrant.GrantId],System.Threading.CancellationToken): System.Threading.Tasks.ValueTask|Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail.AppendAuthorizedAsync(Harborline.Api.Kernel.Audit.AuditRecord,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken,Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision): System.Threading.Tasks.ValueTask|0",
            "apps/local-node-host/Data/Workflow/NodeInvoiceApprovalCutover.cs|Harborline.Api.LocalNodeHost.Data.Workflow.NodeInvoiceApprovalCutover.RecordApprovalAsync(Harborline.Api.Foundation.Authorization.AuthorizationDecision,Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision,System.String,System.Threading.CancellationToken): System.Threading.Tasks.Task|Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail.AppendAuthorizedAsync(Harborline.Api.Kernel.Audit.AuditRecord,Harborline.Api.Foundation.Authorization.AuthorizationDecision,System.Threading.CancellationToken,Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision): System.Threading.Tasks.ValueTask|0",
};
        var actual = discovered
            .Where(site => site.Kind is AuditAppendKind.Authorized or AuditAppendKind.PackAuthorized)
            .Select(SiteKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = allowed.Order(StringComparer.Ordinal).ToArray();
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            string.Join(Environment.NewLine, discovered
                .Where(site => site.Kind is AuditAppendKind.Authorized or AuditAppendKind.PackAuthorized)
                .Select(DescribeSite)));
    }

    private static void AssertNoForbiddenAuthorityReconstructionDependencies(
        IEnumerable<AuditAppendCallSite> discovered)
    {
        var actualOffenders = FindForbiddenAuthorityDependencies(discovered);
        Assert.True(actualOffenders.Length == 0, string.Join(Environment.NewLine, actualOffenders));
        Assert.Equal(["packages/planted/AmbientAuditWriter.cs"], FindForbiddenAuthorityDependencies(
        [
            ("packages/planted/AmbientAuditWriter.cs",
             "async Task Write() { var rows = grantStore as IGrantStore; var now = TimeProvider.System.GetUtcNow(); await audit.AppendAsync(record); }")
        ]));
    }

    private static string[] FindForbiddenAuthorityDependencies(
        IEnumerable<AuditAppendCallSite> sites)
    {
        var offenders = new List<string>();
        foreach (var site in sites)
        {
            var source = ReadSourceMethod(site);
            if (FindForbiddenAuthorityDependencies([(site.File, source)]).Length > 0)
            {
                offenders.Add(site.File);
                continue;
            }

            var parameterList = site.Symbol.IndexOf('(');
            var methodSeparator = site.Symbol.LastIndexOf(
                $".{site.SourceMethod}", parameterList, StringComparison.Ordinal);
            if (methodSeparator < 0) throw new InvalidOperationException($"Could not parse emitted writer {site.Symbol}.");
            var typeName = site.Symbol[..methodSeparator];
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, throwOnError: false))
                .FirstOrDefault(candidate => candidate is not null);
            if (type is null) throw new InvalidOperationException($"Could not resolve emitted writer {typeName}.");
            var usesForbiddenField = type
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Any(field => IsForbiddenAuthorityType(field.FieldType)
                    && source.Contains(field.Name, StringComparison.Ordinal));
            if (usesForbiddenField) offenders.Add(site.File);
        }
        return offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsForbiddenAuthorityType(Type type)
    {
        if (type.IsGenericType)
            return type.GetGenericArguments().Any(IsForbiddenAuthorityType);
        var name = type.FullName ?? type.Name;
        return name == typeof(TimeProvider).FullName
            || name.Contains("GrantStore", StringComparison.Ordinal)
            || name.Contains("AuthorizationClosure", StringComparison.Ordinal)
            || name.Contains("SessionPermissionResolver", StringComparison.Ordinal)
            || name.Contains("AttributionScope", StringComparison.Ordinal)
            || name.Contains("AttributionSource", StringComparison.Ordinal);
    }

    private static string[] FindForbiddenAuthorityDependencies(
        IEnumerable<(string Path, string Source)> methods)
    {
        var forbidden = new[]
        {
            "IGrantStore",
            "IAuthorizationClosure",
            "SelectedSessionPermissionResolver",
            "SessionPermissionResolver",
            "NodeCallerAttributionScope",
            "AmbientNodeCallerAttributionSource",
            "NodePinnedGrantAttribution",
            "TimeProvider",
        };
        return methods
            .Where(method => forbidden.Any(token => method.Source.Contains(token, StringComparison.Ordinal)))
            .Select(method => method.Path)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] FindDecisionIdentityOffenders(IEnumerable<AuditAppendCallSite> sites) =>
        FindDecisionIdentityOffenders(sites.Select(site => (site.File, ReadSourceMethod(site))));

    private static string[] FindDecisionIdentityOffenders(IEnumerable<(string Path, string Source)> methods)
    {
        var offenders = new List<string>();
        foreach (var (path, source) in methods.Distinct())
        {
            var body = source.IndexOf('{');
            var header = body < 0 ? source : source[..body];
            var received = DecisionParameters(header);
            if (received.Count == 0) continue;
            if (source.Contains("DecideAsync(", StringComparison.Ordinal))
            {
                offenders.Add(path);
                continue;
            }

            var position = 0;
            while ((position = source.IndexOf(".AppendAuthorized", position, StringComparison.Ordinal)) >= 0)
            {
                var open = source.IndexOf('(', position);
                if (open < 0) { offenders.Add(path); break; }
                var arguments = SplitArguments(source, open);
                if (arguments.Count < 2 || !received.Contains(arguments[1].Trim(), StringComparer.Ordinal))
                    offenders.Add(path);
                position = open + 1;
            }
        }
        return offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> DecisionParameters(string header)
    {
        var names = new List<string>();
        var position = 0;
        const string typeName = "AuthorizationDecision";
        while ((position = header.IndexOf(typeName, position, StringComparison.Ordinal)) >= 0)
        {
            position += typeName.Length;
            if (position < header.Length && header[position] == '?') position++;
            while (position < header.Length && char.IsWhiteSpace(header[position])) position++;
            var start = position;
            while (position < header.Length && (char.IsLetterOrDigit(header[position]) || header[position] == '_')) position++;
            if (position > start) names.Add(header[start..position]);
        }
        return names;
    }

    private static string ReadSourceMethod(AuditAppendCallSite site)
    {
        var path = Path.Combine(RepositoryRoot(), site.File.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path);
        var lines = source.Split('\n');
        var callOffset = lines.Take(Math.Max(0, site.Line - 1)).Sum(line => line.Length + 1);
        var declaration = source.LastIndexOf(site.SourceMethod + "(", callOffset, StringComparison.Ordinal);
        if (declaration < 0)
            throw new InvalidOperationException($"Could not locate {site.Symbol} at {site.File}:{site.Line}.");
        var start = source.LastIndexOf('\n', declaration);
        start = start < 0 ? 0 : start + 1;
        var open = source.IndexOf('{', declaration);
        if (open < 0) return source[start..Math.Min(source.Length, source.IndexOf('\n', declaration) + 1)];
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[start..(index + 1)];
        }
        throw new InvalidOperationException($"Unterminated source method {site.Symbol}.");
    }

    private static IReadOnlyList<string> SplitArguments(string source, int open)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = open + 1;
        for (var index = open + 1; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' when depth == 0:
                    arguments.Add(source[start..index]);
                    return arguments;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0:
                    arguments.Add(source[start..index]);
                    start = index + 1;
                    break;
            }
        }
        return arguments;
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    internal sealed class MutableDecisionSource(params (string GrantId, long OwnerVersion)[] pins) :
        IAuthorizationClosureSnapshotReader,
        IAuthorizationDefinitionAtomReader
    {
        private PermissionAtom? _requested;
        public List<(string GrantId, long OwnerVersion)> Pins { get; } = [.. pins];
        public int ReadCalls { get; private set; }
        public bool ThrowOnRead { get; set; }

        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request, CancellationToken ct = default)
        {
            if (ThrowOnRead) throw new InvalidOperationException("authorization source read after decision");
            ReadCalls++;
            _requested = request.Act;
            return ValueTask.FromResult(new AuthorizationClosureSnapshot(Pins.Select(pin =>
                new AuthorizationAtomDerivation(
                    request.Act, RoleReference.Administrator, pin.GrantId, pin.OwnerVersion,
                    "definition", request.Target.Scope, request.At.AddMinutes(-1), null)).ToArray()));
        }

        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
            TenantId tenantId, RoleReference role, CancellationToken ct = default)
        {
            if (ThrowOnRead) throw new InvalidOperationException("authorization source read after decision");
            ReadCalls++;
            IReadOnlyList<PermissionAtom> atoms = _requested is { } requested && Pins.Count > 0
                ? [requested]
                : [];
            return ValueTask.FromResult(atoms);
        }
    }

    private sealed class ThrowingAuthorizationSources :
        IAuthorizationClosureSnapshotReader,
        IAuthorizationDefinitionAtomReader,
        IRecordStandingResolver
    {
        public int Calls { get; private set; }
        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(AuthorizationGateRequest request, CancellationToken ct = default) => Throw<AuthorizationClosureSnapshot>();
        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(TenantId tenantId, RoleReference role, CancellationToken ct = default) => Throw<IReadOnlyList<PermissionAtom>>();
        public ValueTask<IReadOnlyList<RecordStanding>> ResolveAsync(AuthorizationGateRequest request, IReadOnlySet<RoleReference> effectiveRecordRoles, CancellationToken ct = default) => Throw<IReadOnlyList<RecordStanding>>();
        private ValueTask<T> Throw<T>()
        {
            Calls++;
            throw new InvalidOperationException("audit path invoked authorization");
        }
    }

    private sealed class RecordingPackInstallAudit : IPackInstallAudit
    {
        public List<AuthorizationDecision> Decisions { get; } = [];
        public AuthorizationDecision? LastDecision => Decisions.LastOrDefault();
        public void Append(PackInstallAuditEntry entry) { }
        public void AppendAuthorized(PackInstallAuditEntry entry, AuthorizationDecision decision) => Decisions.Add(decision);
        public IReadOnlyList<PackInstallAuditEntry> Query(TenantId tenant) => [];
    }
}

internal static class AuditTestCompositionExtensions
{
    internal static IServiceCollection ApplyAuditComposition(
        this IServiceCollection services,
        bool decorated)
    {
        if (decorated)
        {
            services.AddHarborlineKernelAudit();
            services.AddSingleton<IAuditEventReader>(sp =>
                new TrailAuditEventReader(sp.GetRequiredService<IAuditTrail>()));
        }
        else
        {
            services.AddHarborlineKernelAuditReaderInMemory();
        }
        return services;
    }
}

internal sealed class AuditTrailFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    private AuditTrailFixture(ServiceProvider provider)
    {
        _provider = provider;
        Trail = provider.GetRequiredService<IAuditTrail>();
        Authorized = provider.GetRequiredService<IAuthorizedAuditTrail>();
    }

    internal IAuditTrail Trail { get; }
    internal IAuthorizedAuditTrail Authorized { get; }
    internal int InnerAppendCount => _provider.GetRequiredService<RecordingEventLog>().AppendCount;

    internal static AuditTrailFixture Create(bool decorated, IOperationSigner signer)
    {
        var services = new ServiceCollection()
            .AddSingleton<RecordingEventLog>()
            .AddSingleton<IEventLog>(sp => sp.GetRequiredService<RecordingEventLog>())
            .AddSingleton<IOperationVerifier, Ed25519Verifier>()
            .AddSingleton(signer)
            .AddSingleton<IOperationSigner>(signer)
            .ApplyAuditComposition(decorated);
        return new AuditTrailFixture(services.BuildServiceProvider());
    }

    public void Dispose() => _provider.Dispose();
}

internal sealed class RecordingEventLog : IEventLog
{
    private readonly InMemoryEventLog _inner = new();
    internal int AppendCount { get; private set; }
    public ulong CurrentSequence => _inner.CurrentSequence;
    public async Task<ulong> AppendAsync(KernelEvent evt, CancellationToken ct)
    {
        AppendCount++;
        return await _inner.AppendAsync(evt, ct);
    }
    public IAsyncEnumerable<LogEntry> ReadAfterAsync(ulong afterSeq, CancellationToken ct) =>
        _inner.ReadAfterAsync(afterSeq, ct);
    public IAsyncEnumerable<LogEntry> ReadRangeAsync(ulong fromSeq, ulong toSeqInclusive, CancellationToken ct) =>
        _inner.ReadRangeAsync(fromSeq, toSeqInclusive, ct);
    public Task WriteSnapshotAsync(Snapshot snapshot, CancellationToken ct) => _inner.WriteSnapshotAsync(snapshot, ct);
    public Task<Snapshot?> ReadLatestSnapshotAsync(
        string aggregateId, string epochId, string schemaVersion, CancellationToken ct) =>
        _inner.ReadLatestSnapshotAsync(aggregateId, epochId, schemaVersion, ct);
}

internal sealed class TrailAuditEventReader(IAuditTrail trail) : IAuditEventReader
{
    public async Task<AuditRecord?> GetByIdAsync(
        TenantId tenantId,
        Guid auditId,
        DateTimeOffset admittedAt,
        CancellationToken ct = default)
    {
        await foreach (var record in trail.QueryAsync(new AuditQuery(tenantId), ct))
            if (record.AuditId == auditId) return record;
        return null;
    }

    public async Task<AuditEventPage> ListAsync(
        TenantId tenantId,
        AuditEventReaderQuery query,
        CancellationToken ct = default)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in StreamAsync(tenantId, query, ct)) records.Add(record);
        return new AuditEventPage(records.Take(query.PageSize).ToArray(), null, false);
    }

    public async IAsyncEnumerable<AuditRecord> StreamAsync(
        TenantId tenantId,
        AuditEventReaderQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in trail.QueryAsync(new AuditQuery(
            tenantId, query.EventType, query.From, query.To), ct)) records.Add(record);
        foreach (var record in records.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.AuditId))
            yield return record;
    }
}
