using System.Text.Json.Nodes;

using NSubstitute;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Governance.Policy;
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
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.SecurityPolicy.Validation.Validators;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>L1145 end-to-end and compiled-writer coverage for unknown restricting kinds.</summary>
public sealed class RestrictingDefinitionKindAdmissionTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000150");
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "L1145: install refuses an unknown restricting kind before store or projection")]
    public async Task Pack_Unknown_Restricting_Kind_Refuses_Before_Store_Or_Projector()
    {
        const string packageKey = "test.unknown-restricting-kind";
        const string definitionId = "forms.restricted";
        const string unknownKind = "DenyUnlessReviewed";
        var content = JsonNode.Parse($$"""
            {
              "overlay": {
                "rules": [{
                  "id": "rule.future-restriction",
                  "tier": "JsonLogic",
                  "scope": "Schema",
                  "scopeTarget": "",
                  "expression": "true",
                  "action": "{{unknownKind}}"
                }]
              }
            }
            """)!;
        var fixture = await SignedPackAsync(packageKey, definitionId, PackContentKind.FormDefinition, content);
        using (fixture.KeyPair)
        {
            var store = EmptyStore();
            var projector = new ProjectionSpy();
            var kinds = RestrictingDefinitionKindValidator.Shared;
            var installer = new PackInstaller(
                new PackVerifier(new Ed25519Verifier(), new PackFileCodec()),
                store,
                new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator(), kinds),
                new InMemoryPackInstallAudit(),
                TestAuthorization.AllowGate(),
                projector);

            var outcome = installer.Install(fixture.Bytes, fixture.Context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Contains(PackInstallCodes.RefusedAdmission, outcome.RefusalCodes);
            var refusal = Assert.Single(outcome.Preview.AdmissionRefusals);
            Assert.Equal(RestrictingDefinitionKindValidator.KindUnknownCode, refusal.Code);
            Assert.Contains(packageKey, refusal.Message, StringComparison.Ordinal);
            Assert.Contains(definitionId, refusal.Message, StringComparison.Ordinal);
            Assert.Contains(unknownKind, refusal.Message, StringComparison.Ordinal);
            store.DidNotReceiveWithAnyArgs().Commit(default!);
            Assert.Equal(0, projector.ProjectCount);
        }
    }

    [Fact(DisplayName = "ADR 0038 control: an unknown permitting view kind remains inert at admission")]
    public void Permit_Unknown_View_Kind_Still_Fails_Open()
    {
        var admission = new PackRestrictingDefinitionAdmission(RestrictingDefinitionKindValidator.Shared);
        var composed = new[]
        {
            new PackComposedItem(
                "test.future-view",
                "view.future",
                PackContentKind.ViewDefinition,
                "1.0.0",
                """{"viewKind":"views.future/not-installed","parameters":{}}"""),
        };

        Assert.Empty(admission.Validate(composed));
    }

    [Fact(DisplayName = "L1145: the compiled rule writer refuses an unknown action kind")]
    public void Compiled_Rule_Writer_Uses_Restricting_Kind_Validator()
    {
        var registry = new Harborline.Api.Foundation.RuleEngine.Registry.RuleRegistry(
            RestrictingDefinitionKindValidator.Shared);
        var rule = new Harborline.Api.Foundation.Forms.Models.RuleDefinition(
            "rule.unknown",
            Harborline.Api.Foundation.Forms.Models.RuleTier.JsonLogic,
            Harborline.Api.Foundation.Forms.Models.RuleScope.Schema,
            "",
            "true",
            (Harborline.Api.Foundation.Forms.Models.RuleActionKind)999);

        var exception = Assert.Throws<UnknownRestrictingDefinitionKindException>(() =>
            registry.Publish("tenant", new Harborline.Api.Foundation.RuleEngine.Registry.PublishedRuleVersion(
                "rule.unknown", "1.0.0", false, rule)));

        Assert.Equal(RestrictingDefinitionKindValidator.KindUnknownCode, exception.Refusal.Code);
        Assert.Contains("999", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "L1145: the compiled policy writer refuses an unknown effect kind")]
    public void Compiled_Policy_Writer_Uses_Restricting_Kind_Validator()
    {
        var binding = new PolicyBinding(
            new Harborline.Api.Foundation.Forms.Models.Tag("test/policy", "policy.unknown"),
            null,
            [new PolicyEffect((EffectKind)999, [Trigger.Store])]);

        var exception = Assert.Throws<UnknownRestrictingDefinitionKindException>(() =>
            new InMemoryPolicyRegistry(false, [binding], RestrictingDefinitionKindValidator.Shared));

        Assert.Equal(RestrictingDefinitionKindValidator.KindUnknownCode, exception.Refusal.Code);
        Assert.Contains("policy.unknown", exception.Message, StringComparison.Ordinal);
        Assert.Contains("999", exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "L1145: the retention writer uses the same unknown-kind validator")]
    public async Task Retention_Writer_Uses_Restricting_Kind_Validator()
    {
        var current = TenantSecurityPolicy.DefaultFor(Tenant, Now);
        var proposed = current with
        {
            AuditRetention = current.AuditRetention with
            {
                JurisdictionPreset = (RetentionJurisdictionPreset)999,
            },
        };
        var result = await new SchemaValidator(RestrictingDefinitionKindValidator.Shared).ValidateAsync(
            proposed,
            current,
            new Harborline.Api.Foundation.SecurityPolicy.Validation.SecurityPolicyValidationContext(
                Tenant,
                new ActorId("test-actor"),
                Harborline.Api.Foundation.Ship.Common.ShipRole.Captain));

        var finding = Assert.Single(
            result.Findings,
            finding => finding.Code == RestrictingDefinitionKindValidator.KindUnknownCode);
        Assert.Contains("999", finding.Message, StringComparison.Ordinal);
    }

    private static IPackInstallMutationStore EmptyStore()
    {
        var store = Substitute.For<IPackInstallMutationStore>();
        store.ListInstalled(Arg.Any<TenantId>()).Returns(Array.Empty<InstalledPack>());
        store.GetOverrides(Arg.Any<TenantId>(), Arg.Any<string>()).Returns(Array.Empty<PackTenantOverride>());
        store.GetKeyOwnership(Arg.Any<TenantId>()).Returns(new Dictionary<string, string>());
        return store;
    }

    private sealed class ProjectionSpy : IPackProjectionDispatcher
    {
        public int ProjectCount { get; private set; }

        public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default)
        {
            ProjectCount++;
            return null;
        }
    }

    private static async Task<(byte[] Bytes, PackInstallContext Context, KeyPair KeyPair)> SignedPackAsync(
        string packageKey,
        string definitionId,
        PackContentKind kind,
        JsonNode content)
    {
        var keyPair = KeyPair.Generate();
        var codec = new PackFileCodec();
        var export = await new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System).ExportAsync(
                new PackExportRequest(
                    packageKey,
                    "1.0.0",
                    "Unknown restricting kind",
                    "L1145 fixture",
                    PackScopeTier.Vertical,
                    [new PackContentSource(definitionId, kind, "1.0.0", content)],
                    Array.Empty<PackDependencyRef>(),
                    Array.Empty<string>(),
                    Epoch: 1,
                    Dcp: DomainComplianceProfile.General("test-author")),
                new Ed25519Signer(keyPair));
        Assert.True(export.Succeeded, string.Join("; ", export.Validation.Errors.Select(e => e.Message)));

        var trust = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(TrustScope.OwnRoster, keyPair.PrincipalId, 1, TrustRootStatus.Current),
        ]);
        var context = new PackInstallContext(
            Tenant,
            trust,
            PackRevocationList.Empty,
            Now,
            TimeSpan.FromDays(30),
            Principal: "test-operator");
        return (export.FileBytes!, context, keyPair);
    }
}
