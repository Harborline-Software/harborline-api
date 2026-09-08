using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Versions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using NSubstitute;
using System.Text;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class RoleGateAdmissionTests
{
    private static readonly TenantId Tenant = new("role-gate-tenant");
    private static readonly DateTimeOffset At = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, CascadeLayer, string, bool, string?> Matrix => new()
    {
        { "platform-role", CascadeLayer.Base, RoleReference.Administrator.ToString(), true, null },
        { "platform-standing", CascadeLayer.Base, "standing:handler", true, null },
        { "platform-tenant-role", CascadeLayer.Base, "tax.roles/tenant-operator", false, RoleGateAdmissionRules.PlatformRoleOrStandingOnly },
        { "vendor-own", CascadeLayer.Pack, "tax.roles/vendor-a-operator", true, null },
        { "vendor-tenant", CascadeLayer.Pack, "tax.roles/tenant-operator", true, null },
        { "vendor-other", CascadeLayer.Pack, "tax.roles/vendor-b-operator", false, RoleGateAdmissionRules.VendorOwnOrTenantRoleOnly },
        { "unresolved", CascadeLayer.Tenant, "tax.roles/missing", false, RoleGateAdmissionRules.UnresolvedRole },
    };

    public static TheoryData<string, string> FormLifecycleOverloads => new()
    {
        { "register", "route" }, { "register", "compiled" }, { "register", "pack" },
        { "register-publish", "route" }, { "register-publish", "compiled" }, { "register-publish", "pack" },
        { "publish", "route" }, { "publish", "compiled" }, { "publish", "pack" },
        { "withdraw", "route" }, { "withdraw", "compiled" }, { "withdraw", "pack" },
        { "restore", "route" }, { "restore", "compiled" }, { "restore", "pack" },
        { "replace", "route" }, { "replace", "compiled" }, { "replace", "pack" },
        { "deprecate", "route" },
    };

    public static TheoryData<string, string> WorkflowLifecycleOverloads => new()
    {
        { "register", "route" }, { "register", "compiled" }, { "register", "pack" },
        { "register-publish", "route" }, { "register-publish", "compiled" }, { "register-publish", "pack" },
        { "publish", "route" }, { "publish", "compiled" }, { "publish", "pack" },
        { "withdraw", "route" }, { "withdraw", "pack" },
        { "restore", "pack" },
        { "replace", "route" }, { "replace", "compiled" }, { "replace", "pack" },
    };

    public static IEnumerable<object[]> PrivilegedFormMutationOverloads()
    {
        foreach (var layer in new[] { CascadeLayer.Base, CascadeLayer.Pack })
        {
            yield return ["publish", "context", layer, FormDefinitionStatus.Draft];
            yield return ["publish", "compiled", layer, FormDefinitionStatus.Draft];
            yield return ["withdraw", "context", layer, FormDefinitionStatus.Published];
            yield return ["withdraw", "compiled", layer, FormDefinitionStatus.Published];
            yield return ["restore", "context", layer, FormDefinitionStatus.Withdrawn];
            yield return ["restore", "compiled", layer, FormDefinitionStatus.Withdrawn];
            yield return ["deprecate", "context", layer, FormDefinitionStatus.Published];
            yield return ["replace", "context", layer, FormDefinitionStatus.Published];
            yield return ["replace", "compiled", layer, FormDefinitionStatus.Published];
        }
    }

    public static IEnumerable<object[]> PrivilegedWorkflowMutationOverloads()
    {
        foreach (var layer in new[] { CascadeLayer.Base, CascadeLayer.Pack })
        {
            yield return ["publish", "context", layer, WorkflowDefinitionStatus.Draft];
            yield return ["publish", "compiled", layer, WorkflowDefinitionStatus.Draft];
            yield return ["withdraw", "context", layer, WorkflowDefinitionStatus.Published];
            yield return ["replace", "context", layer, WorkflowDefinitionStatus.Published];
            yield return ["replace", "compiled", layer, WorkflowDefinitionStatus.Published];
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task AdmissionMatrix_UsesAuthorityDerivedOwnerAndRefusesBeforeStore(
        string scenario,
        CascadeLayer layer,
        string subject,
        bool allowed,
        string? rule)
    {
        var writerEvents = new List<string>();
        var store = new EntityStoreFormDefinitionStore(
            new OrderedEntityStore(
                new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System), writerEvents),
            TimeProvider.System);
        var admission = Admission();
        var lifecycle = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), admission);
        Assert.Same(admission, lifecycle.RoleGateAdmission);
        var definition = Definition(scenario, layer, subject);
        RoleGateAdmissionException? refused = null;
        PackSeedProjectionSummary? projection = null;
        (HttpStatusCode Status, JsonElement Body)? route = null;

        try
        {
            if (layer == CascadeLayer.Base)
            {
                var authority = await PlatformFormAuthority(lifecycle, definition.Id.Value);
                await lifecycle.RegisterAndPublishAsync(definition, authority);
            }
            else if (layer == CascadeLayer.Pack)
            {
                projection = await ProjectThroughRealPackProjector(
                    definition.Id.Value, subject, store, lifecycle, admission);
            }
            else
            {
                route = await PutThroughRealFormRoute(
                    definition.Id.Value, subject, lifecycle, admission);
            }
        }
        catch (RoleGateAdmissionException ex)
        {
            refused = ex;
        }

        if (allowed)
        {
            Assert.Null(refused);
            if (layer == CascadeLayer.Pack) Assert.Empty(projection!.Refusals);
            Assert.Single(await Definitions(store));
        }
        else
        {
            if (layer == CascadeLayer.Tenant)
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, route!.Value.Status);
                Assert.Equal($"authorization.role_gate.{rule}", route.Value.Body.GetProperty("code").GetString());
                // 094: the refusal's machine-read facts live under the envelope's `detail` object, not
                // flat beside `code`. Same facts, stable place.
                var refusalDetail = route.Value.Body.GetProperty("detail");
                Assert.Equal(subject, refusalDetail.GetProperty("role").GetString());
                Assert.Equal(rule, refusalDetail.GetProperty("rule").GetString());
                Assert.Empty(await Definitions(store));
                Assert.DoesNotContain("writer", writerEvents);
                return;
            }
            if (layer == CascadeLayer.Pack)
            {
                var projectionRefusal = Assert.Single(projection!.Refusals);
                Assert.Equal($"authorization.role_gate.{rule}", projectionRefusal.Code);
                Assert.Empty(await Definitions(store));
                Assert.DoesNotContain("writer", writerEvents);
                return;
            }
            Assert.NotNull(refused);
            Assert.Equal($"authorization.role_gate.{rule}", refused.Code);
            Assert.Equal(definition.Id.Value, refused.Finding.DefinitionId);
            Assert.Equal("section:main.read", refused.Finding.Gate);
            Assert.Equal(subject, refused.Finding.Subject);
            Assert.Equal(rule, refused.Finding.Rule);
            Assert.Empty(await Definitions(store));
            Assert.DoesNotContain("writer", writerEvents);
        }
    }

    [Fact]
    public async Task TenantCompiledDefinitionClaimingBaseIsRefusedBeforeStore()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var lifecycle = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), Admission());
        var definition = Definition("forged-tenant", CascadeLayer.Base, RoleReference.Administrator.ToString());
        var authority = await lifecycle.DecideAsync(definition.Id.Value, Authority());

        var refusal = await Assert.ThrowsAsync<DefinitionProvenanceException>(async () =>
            await lifecycle.RegisterAndPublishAsync(definition, authority));

        Assert.Equal(DefinitionAuthorityClassifier.LayerMismatchCode, refusal.Code);
        Assert.Empty(await Definitions(store));
    }

    [Fact]
    public async Task VendorPackClaimingBaseIsRefusedBeforeStore()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var lifecycle = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), Admission());
        var definition = Definition("forged-pack", CascadeLayer.Base, RoleReference.Administrator.ToString());

        var refusal = await Assert.ThrowsAsync<DefinitionProvenanceException>(async () =>
            await lifecycle.RegisterAndPublishAsync(definition, await PackAuthority()));

        Assert.Equal("vendor-a", refusal.PackageId);
        Assert.Empty(await Definitions(store));
    }

    [Fact]
    public async Task PlatformSeedAuthorityStampsBaseWithoutCallerFlag()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var lifecycle = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), Admission());
        var definition = Definition("platform-seed", CascadeLayer.Tenant, RoleReference.Administrator.ToString());
        var authority = await PlatformFormAuthority(lifecycle, definition.Id.Value);

        await lifecycle.RegisterAndPublishAsync(definition, authority);

        var written = await store.GetAsync(new DefinitionCoordinates(Tenant, definition.Id.Value, "1.0.0"));
        Assert.Equal(CascadeLayer.Base, written.Envelope.CascadeLayer);
    }

    [Theory]
    [MemberData(nameof(PrivilegedFormMutationOverloads))]
    public async Task TenantAuthorityCannotMutateStoredPrivilegedForm(
        string operation, string lane, CascadeLayer layer, FormDefinitionStatus status)
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var id = $"privileged-{layer}-{operation}";
        var persisted = Definition(id, layer, RoleReference.Administrator.ToString()) with { Status = status };
        await store.RegisterAsync(persisted);
        var lifecycle = TestAuthorization.FormLifecycle(
            store, TestAuthorization.AllowGate(), Admission());
        var coordinates = new DefinitionCoordinates(Tenant, id, "1.0.0");
        var context = Authority();
        var compiled = await lifecycle.DecideAsync(id, context);

        var refusal = await Assert.ThrowsAsync<DefinitionProvenanceException>(async () =>
        {
            if (operation == "publish" && lane == "context") await lifecycle.PublishAsync(coordinates, context);
            else if (operation == "publish") await lifecycle.PublishAsync(coordinates, compiled);
            else if (operation == "withdraw" && lane == "context") await lifecycle.WithdrawAsync(coordinates, context);
            else if (operation == "withdraw") await lifecycle.WithdrawAsync(coordinates, compiled);
            else if (operation == "restore" && lane == "context") await lifecycle.RestorePackProjectionAsync(coordinates, context);
            else if (operation == "restore") await lifecycle.RestorePackProjectionAsync(coordinates, compiled);
            else if (operation == "deprecate") await lifecycle.DeprecateAsync(coordinates, context);
            else
            {
                var replacement = Definition(id, CascadeLayer.Tenant, "tax.roles/tenant-operator") with
                {
                    Envelope = Definition(id, CascadeLayer.Tenant, "tax.roles/tenant-operator").Envelope with
                    {
                        Version = new SemanticVersion(2, 0, 0),
                    },
                };
                if (lane == "context") await lifecycle.RegisterAsync(replacement, context);
                else await lifecycle.RegisterAsync(replacement, compiled);
            }
        });

        Assert.Equal(DefinitionAuthorityClassifier.LayerMismatchCode, refusal.Code);
        Assert.Equal(status, (await store.GetAsync(coordinates)).Status);
        Assert.DoesNotContain(await Definitions(store), item => item.Version == new SemanticVersion(2, 0, 0));
    }

    [Theory]
    [MemberData(nameof(PrivilegedWorkflowMutationOverloads))]
    public async Task TenantAuthorityCannotMutateStoredPrivilegedWorkflow(
        string operation, string lane, CascadeLayer layer, WorkflowDefinitionStatus status)
    {
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var store = new EntityStoreWorkflowDefinitionStore(
            entities, new WorkflowAdmissionValidator(), TimeProvider.System);
        var id = $"privileged-{layer}-{operation}";
        using var authored = WorkflowAuthored(id, "1.0.0", RoleReference.Administrator.ToString());
        var persisted = WorkflowDefinitionWireMapper.ToModel(
            authored.RootElement, Tenant.Value, id, "1.0.0", layer);
        if (layer == CascadeLayer.Pack)
        {
            persisted = new WorkflowDefinition
            {
                Envelope = persisted.Envelope,
                Status = persisted.Status,
                SubjectFormRef = persisted.SubjectFormRef,
                Mutability = persisted.Mutability,
                InitialState = persisted.InitialState,
                States = persisted.States,
                Transitions = persisted.Transitions,
                Triggers = persisted.Triggers,
                Actions = persisted.Actions,
                GuardRuleIds = persisted.GuardRuleIds,
                PackSource = new PackProjectionSource("vendor-a", "1.0.0"),
            };
        }
        await store.RegisterAsync(
            persisted, authored.RootElement, new WorkflowDefinitionRegistrationOptions(At, status));
        var lifecycle = TestAuthorization.WorkflowLifecycle(
            store, TestAuthorization.AllowGate(), Admission());
        var coordinates = new DefinitionCoordinates(Tenant, id, "1.0.0");
        var context = Authority();
        var compiled = await lifecycle.DecideAsync(id, context);

        var refusal = await Assert.ThrowsAsync<DefinitionProvenanceException>(async () =>
        {
            if (operation == "publish" && lane == "context") await lifecycle.PublishAsync(coordinates, context);
            else if (operation == "publish") await lifecycle.PublishAsync(coordinates, compiled);
            else if (operation == "withdraw") await lifecycle.WithdrawAsync(coordinates, context);
            else
            {
                using var replacementAuthored = WorkflowAuthored(id, "2.0.0", "tax.roles/tenant-operator");
                if (lane == "context") await lifecycle.RegisterAsync(replacementAuthored.RootElement, context);
                else await lifecycle.RegisterAsync(replacementAuthored.RootElement, compiled);
            }
        });

        Assert.Equal(DefinitionAuthorityClassifier.LayerMismatchCode, refusal.Code);
        Assert.Equal(status, (await store.GetAsync(coordinates)).Status);
        Assert.DoesNotContain(await WorkflowDefinitions(store), item => item.Version == "2.0.0");
    }

    [Fact]
    public async Task PackFormSupersessionRefusesAnotherPackagesPublishedHead()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var head = Definition("foreign-pack-head-form", CascadeLayer.Pack, RoleReference.Administrator.ToString()) with
        {
            Status = FormDefinitionStatus.Published,
            PackSource = new PackProjectionSource("vendor-b", "1.0.0"),
        };
        await store.RegisterAsync(head);
        var lifecycle = TestAuthorization.FormLifecycle(
            store, TestAuthorization.AllowGate(), Admission());
        var replacement = Definition("foreign-pack-head-form", CascadeLayer.Pack, "tax.roles/vendor-a-operator") with
        {
            Envelope = head.Envelope with { Version = new SemanticVersion(2, 0, 0) },
            PackSource = new PackProjectionSource("vendor-a", "1.0.0"),
        };

        var refusal = await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
            await lifecycle.RegisterAsync(replacement, await PackAuthority()));

        Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, refusal.Code);
        Assert.DoesNotContain(await Definitions(store), item => item.Version == new SemanticVersion(2, 0, 0));
    }

    [Fact]
    public async Task PackWorkflowSupersessionRefusesAnotherPackagesPublishedHead()
    {
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var store = new EntityStoreWorkflowDefinitionStore(
            entities, Substitute.For<IWorkflowAdmissionValidator>(), TimeProvider.System);
        using var headAuthored = OrderedWorkflowAuthored("foreign-pack-head-workflow", "1.0.0", pack: true);
        var head = OrderedWorkflow("foreign-pack-head-workflow", "1.0.0", pack: true);
        head = CopyPackSource(head, new PackProjectionSource("vendor-b", "1.0.0"));
        await store.RegisterAsync(
            head,
            headAuthored.RootElement,
            new WorkflowDefinitionRegistrationOptions(At, WorkflowDefinitionStatus.Published));
        var lifecycle = TestAuthorization.WorkflowLifecycle(
            store, TestAuthorization.AllowGate(), Admission());
        using var replacementAuthored = OrderedWorkflowAuthored(
            "foreign-pack-head-workflow", "2.0.0", pack: true);

        var refusal = await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
            await lifecycle.RegisterAsync(replacementAuthored.RootElement, await PackAuthority()));

        Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, refusal.Code);
        Assert.DoesNotContain(await WorkflowDefinitions(store), item => item.Version == "2.0.0");
    }

    [Fact]
    public void RoleAndStandingWireShapesCannotChangeLanes()
    {
        var standing = Assert.Throws<GateReferenceShapeException>(() =>
            new RecordStandingReference(RoleReference.Administrator.ToString()));
        Assert.Equal(GateReferenceShapeCodes.InvalidStanding, standing.Code);
        Assert.Equal("requiredStandings", standing.Field);

        var role = Assert.Throws<GateReferenceShapeException>(() =>
            DeclarativeGateReference.ForRole("requiredRoles", "handler"));
        Assert.Equal(GateReferenceShapeCodes.InvalidRole, role.Code);
        Assert.Equal("requiredRoles", role.Field);
    }

    [Theory]
    [InlineData("requiredRoles", "[\"workflow.roles/approver\",17]", "action.requiredRoles", 1)]
    [InlineData("requiredRoles", "[\"workflow.roles/approver\",\" \"]", "action.requiredRoles", 1)]
    [InlineData("requiredStandings", "[\"handler\",17]", "action.requiredStandings", 1)]
    [InlineData("requiredStandings", "[\"handler\",\" \"]", "action.requiredStandings", 1)]
    public void WorkflowWireMapperRefusesMalformedTypedLaneEntries(
        string property, string lane, string expectedField, int expectedIndex)
    {
        using var authored = JsonDocument.Parse($$"""
            {
              "states": [], "transitions": [], "triggers": [],
              "actions": [{ "id": "notify", "on": {}, "{{property}}": {{lane}} }]
            }
            """);

        var refusal = Assert.Throws<WorkflowGateLaneException>(() =>
            WorkflowDefinitionWireMapper.ToModel(authored.RootElement, Tenant.Value, "typed-lane", "1.0.0"));
        Assert.Equal(WorkflowGateLaneException.StableCode, refusal.Code);
        Assert.Equal(expectedField, refusal.Field);
        Assert.Equal(expectedIndex, refusal.Index);
    }

    [Fact]
    public async Task AdmissionRunsBeforeTheInternalStoreWrite()
    {
        var order = new List<string>();
        var entities = Substitute.For<IEntityMutationStore>();
        entities.GetAsync(Arg.Any<EntityId>(), Arg.Any<VersionSelector>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Entity?>(null));
        entities.CreateAsync(Arg.Any<SchemaId>(), Arg.Any<JsonDocument>(), Arg.Any<CreateOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                order.Add("store");
                return Task.FromResult(new EntityId("test", "ordered", "1"));
            });
        var store = new EntityStoreFormDefinitionStore(entities, TimeProvider.System);
        var lifecycle = TestAuthorization.FormLifecycle(
            store,
            TestAuthorization.AllowGate(),
            new OrderedAdmission(order));

        await lifecycle.RegisterAndPublishAsync(
            Definition("ordered", CascadeLayer.Tenant, RoleReference.Administrator.ToString()),
            Authority());

        Assert.Equal(["admission", "store"], order);
    }

    [Theory]
    [MemberData(nameof(FormLifecycleOverloads))]
    public async Task EveryFormLifecycleOverloadOrdersAdmissionAndLegalHoldBeforeWriter(
        string operation, string lane)
    {
        var order = new List<string>();
        var entities = new OrderedEntityStore(
            new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System), order);
        var store = new EntityStoreFormDefinitionStore(entities, TimeProvider.System);
        var lifecycle = TestAuthorization.FormLifecycle(
            store, TestAuthorization.AllowGate(), new OrderedAdmission(order), new OrderedLegalHold(order));
        var id = $"ordered-{operation}-{lane}";
        var isPack = lane == "pack";
        var layer = isPack ? CascadeLayer.Pack : CascadeLayer.Tenant;
        var subject = isPack ? "tax.roles/vendor-a-operator" : "tax.roles/tenant-operator";
        var definition = Definition(id, layer, subject);
        if (!isPack && operation == "restore")
        {
            definition = definition with { PackSource = new PackProjectionSource("historical", "1.0.0") };
        }
        var coordinates = new DefinitionCoordinates(Tenant, id, "1.0.0");
        var routeAuthority = Authority();
        var compiledAuthority = await lifecycle.DecideAsync(id, routeAuthority);
        var packAuthority = await PackAuthority();

        if (operation is "publish" or "withdraw" or "restore" or "deprecate" or "replace")
        {
            var status = operation switch
            {
                "publish" => FormDefinitionStatus.Draft,
                "restore" => FormDefinitionStatus.Withdrawn,
                _ => FormDefinitionStatus.Published,
            };
            await store.RegisterAsync(definition with { Status = status });
        }
        order.Clear();

        if (operation == "register")
        {
            if (lane == "route") await lifecycle.RegisterAsync(definition, routeAuthority);
            else if (lane == "compiled") await lifecycle.RegisterAsync(definition, compiledAuthority);
            else await lifecycle.RegisterAsync(definition, packAuthority);
        }
        else if (operation == "register-publish")
        {
            if (lane == "route") await lifecycle.RegisterAndPublishAsync(definition, routeAuthority);
            else if (lane == "compiled") await lifecycle.RegisterAndPublishAsync(definition, compiledAuthority);
            else await lifecycle.RegisterAndPublishAsync(definition, packAuthority);
        }
        else if (operation == "publish")
        {
            if (lane == "route") await lifecycle.PublishAsync(coordinates, routeAuthority);
            else if (lane == "compiled") await lifecycle.PublishAsync(coordinates, compiledAuthority);
            else await lifecycle.PublishAsync(definition, packAuthority);
        }
        else if (operation == "withdraw")
        {
            if (lane == "route") await lifecycle.WithdrawAsync(coordinates, routeAuthority);
            else if (lane == "compiled") await lifecycle.WithdrawAsync(coordinates, compiledAuthority);
            else await lifecycle.WithdrawAsync(definition, packAuthority);
        }
        else if (operation == "restore")
        {
            if (lane == "route") await lifecycle.RestorePackProjectionAsync(coordinates, routeAuthority);
            else if (lane == "compiled") await lifecycle.RestorePackProjectionAsync(coordinates, compiledAuthority);
            else await lifecycle.RestorePackProjectionAsync(definition, packAuthority);
        }
        else if (operation == "deprecate")
        {
            await lifecycle.DeprecateAsync(coordinates, routeAuthority);
        }
        else
        {
            var replacement = definition with
            {
                Envelope = definition.Envelope with { Version = new SemanticVersion(2, 0, 0) },
                Status = FormDefinitionStatus.Draft,
            };
            if (lane == "route") await lifecycle.RegisterAsync(replacement, routeAuthority);
            else if (lane == "compiled") await lifecycle.RegisterAsync(replacement, compiledAuthority);
            else await lifecycle.RegisterAsync(replacement, packAuthority);
        }

        Assert.Contains("admission", order);
        Assert.Contains("writer", order);
        Assert.True(order.IndexOf("admission") < order.IndexOf("writer"), string.Join(",", order));
        if (operation is "withdraw" or "deprecate" or "replace")
        {
            Assert.Contains("legal-hold", order);
            Assert.True(order.IndexOf("legal-hold") < order.IndexOf("writer"), string.Join(",", order));
        }
    }

    [Theory]
    [MemberData(nameof(WorkflowLifecycleOverloads))]
    public async Task EveryWorkflowLifecycleOverloadOrdersAdmissionBeforeWriter(
        string operation, string lane)
    {
        var order = new List<string>();
        var entities = new OrderedEntityStore(
            new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System), order);
        var store = new EntityStoreWorkflowDefinitionStore(
            entities, Substitute.For<IWorkflowAdmissionValidator>(), TimeProvider.System);
        var lifecycle = TestAuthorization.WorkflowLifecycle(
            store, TestAuthorization.AllowGate(), new OrderedAdmission(order));
        var id = $"ordered-workflow-{operation}-{lane}";
        var definition = OrderedWorkflow(id, "1.0.0", lane == "pack");
        using var authored = OrderedWorkflowAuthored(id, "1.0.0", lane == "pack");
        var coordinates = new DefinitionCoordinates(Tenant, id, "1.0.0");
        var routeAuthority = Authority();
        var compiledAuthority = await lifecycle.DecideAsync(id, routeAuthority);
        var packAuthority = await PackAuthority();

        if (operation is "publish" or "withdraw" or "restore" or "replace")
        {
            var status = operation switch
            {
                "publish" => WorkflowDefinitionStatus.Draft,
                "restore" => WorkflowDefinitionStatus.Withdrawn,
                _ => WorkflowDefinitionStatus.Published,
            };
            await store.RegisterAsync(
                definition, authored.RootElement,
                new WorkflowDefinitionRegistrationOptions(At, status));
        }
        order.Clear();

        if (operation == "register")
        {
            if (lane == "route") await lifecycle.RegisterAsync(authored.RootElement, routeAuthority);
            else if (lane == "compiled") await lifecycle.RegisterAsync(authored.RootElement, compiledAuthority);
            else await lifecycle.RegisterAsync(authored.RootElement, packAuthority);
        }
        else if (operation == "register-publish")
        {
            if (lane == "route") await lifecycle.RegisterAndPublishAsync(authored.RootElement, routeAuthority);
            else if (lane == "compiled") await lifecycle.RegisterAndPublishAsync(authored.RootElement, compiledAuthority);
            else await lifecycle.RegisterAndPublishAsync(authored.RootElement, packAuthority);
        }
        else if (operation == "publish")
        {
            if (lane == "route") await lifecycle.PublishAsync(coordinates, routeAuthority);
            else if (lane == "compiled") await lifecycle.PublishAsync(coordinates, compiledAuthority);
            else await lifecycle.PublishAsync(authored.RootElement, packAuthority);
        }
        else if (operation == "withdraw")
        {
            if (lane == "route") await lifecycle.WithdrawAsync(coordinates, routeAuthority);
            else await lifecycle.WithdrawAsync(authored.RootElement, packAuthority);
        }
        else if (operation == "restore")
        {
            await lifecycle.RestorePackProjectionAsync(authored.RootElement, packAuthority);
        }
        else
        {
            var replacement = OrderedWorkflow(id, "2.0.0", lane == "pack");
            using var replacementAuthored = OrderedWorkflowAuthored(id, "2.0.0", lane == "pack");
            if (lane == "route") await lifecycle.RegisterAsync(replacementAuthored.RootElement, routeAuthority);
            else if (lane == "compiled") await lifecycle.RegisterAsync(replacementAuthored.RootElement, compiledAuthority);
            else await lifecycle.RegisterAsync(replacementAuthored.RootElement, packAuthority);
        }

        Assert.Contains("admission", order);
        Assert.Contains("writer", order);
        Assert.True(order.IndexOf("admission") < order.IndexOf("writer"), string.Join(",", order));
    }

    [Fact]
    public async Task WorkflowRegistrationMapsAndRefusesHostileAuthoredWireBeforePersistence()
    {
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var store = new EntityStoreWorkflowDefinitionStore(
            entities, Substitute.For<IWorkflowAdmissionValidator>(), TimeProvider.System);
        var admission = Admission();
        var lifecycle = TestAuthorization.WorkflowLifecycle(store, TestAuthorization.AllowGate(), admission);
        using var authored = WorkflowAuthored("workflow-role-gate", "1.0.0", "tax.roles/missing");

        var refusal = await Assert.ThrowsAsync<RoleGateAdmissionException>(async () =>
            await lifecycle.RegisterAndPublishAsync(authored.RootElement, Authority()));

        Assert.Same(admission, lifecycle.RoleGateAdmission);
        Assert.Equal("action:notify", refusal.Finding.Gate);
        Assert.Empty(await WorkflowDefinitions(store));
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("withdraw")]
    [InlineData("restore")]
    public async Task PackWorkflowTransitionRefusesCallerWireThatDiffersOnlyInMapperIgnoredChrome(
        string operation)
    {
        var entities = new OrderedEntityStore(
            new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System), []);
        var store = new EntityStoreWorkflowDefinitionStore(
            entities, Substitute.For<IWorkflowAdmissionValidator>(), TimeProvider.System);
        var id = "pack-full-wire-" + operation;
        using var authored = OrderedWorkflowAuthored(id, "1.0.0", pack: true);
        var persistedModel = OrderedWorkflow(id, "1.0.0", pack: true);
        var status = operation switch
        {
            "publish" => WorkflowDefinitionStatus.Draft,
            "withdraw" => WorkflowDefinitionStatus.Published,
            _ => WorkflowDefinitionStatus.Withdrawn,
        };
        await store.RegisterAsync(
            persistedModel,
            authored.RootElement,
            new WorkflowDefinitionRegistrationOptions(At, status));
        var lifecycle = TestAuthorization.WorkflowLifecycle(
            store, TestAuthorization.AllowGate(), Admission());
        var callerNode = JsonNode.Parse(authored.RootElement.GetRawText())!.AsObject();
        callerNode["title"] = "mapper-ignored caller chrome";
        using var caller = JsonDocument.Parse(callerNode.ToJsonString());
        var authority = await PackAuthority();

        var refusal = await Assert.ThrowsAsync<PackProjectionAuthorityException>(async () =>
        {
            if (operation == "publish")
                await lifecycle.PublishAsync(caller.RootElement, authority);
            else if (operation == "withdraw")
                await lifecycle.WithdrawAsync(caller.RootElement, authority);
            else
                await lifecycle.RestorePackProjectionAsync(caller.RootElement, authority);
        });

        Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, refusal.Code);
        Assert.Equal(status,
            (await store.GetAsync(new DefinitionCoordinates(Tenant, id, "1.0.0"))).Status);
    }

    [Fact]
    public async Task AuthorizationHealthReadsPublishedStoreStateAndSurvivesAdmissionRestart()
    {
        var forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        var vocabulary = new MutableRoleVocabulary(AdmissionVocabulary());
        var admission = new RoleGateAdmission(vocabulary, forms);
        var lifecycle = TestAuthorization.FormLifecycle(
            forms,
            TestAuthorization.AllowGate(),
            admission);
        var invalid = Definition("ownerless", CascadeLayer.Pack, "tax.roles/vendor-a-operator");
        await lifecycle.RegisterAndPublishAsync(invalid, await PackAuthority());
        vocabulary.Remove(new RoleReference("tax.roles", "vendor-a-operator"));
        var check = new AuthorizationHealthCheck(admission);

        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, unhealthy.Status);
        var findings = Assert.IsType<Dictionary<string, object?>[]>(unhealthy.Data["authorizationFindings"]);
        Assert.Equal(2, findings.Length);
        Assert.All(findings, finding =>
        {
            Assert.Equal("ownerless", finding["definitionId"]);
            Assert.Equal("authorization.role_gate.unresolved_role", finding["code"]);
            Assert.Equal("vendor-a", finding["packageId"]);
        });

        var restarted = new AuthorizationHealthCheck(new RoleGateAdmission(vocabulary, forms));
        Assert.Equal(HealthStatus.Degraded, (await restarted.CheckHealthAsync(new HealthCheckContext())).Status);

        var replacementDraft = Definition("ownerless", CascadeLayer.Pack, "tax.roles/tenant-operator");
        var replacement = replacementDraft with
        {
            Envelope = replacementDraft.Envelope with { Version = new SemanticVersion(2, 0, 0) },
        };
        await lifecycle.RegisterAndPublishAsync(replacement, await PackAuthority());

        var healthy = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
    }

    [Fact]
    public async Task AuthorizationHealthInventoriesWorkflowActionsAndFallsBackAfterWithdrawal()
    {
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var workflows = new EntityStoreWorkflowDefinitionStore(
            entities, new WorkflowAdmissionValidator(), TimeProvider.System);

        using var validAuthored = WorkflowAuthored("health-workflow", "1.0.0", RoleReference.Administrator.ToString());
        var valid = WorkflowDefinitionWireMapper.ToModel(
            validAuthored.RootElement, Tenant.Value, "health-workflow", "1.0.0");
        await workflows.RegisterAsync(
            valid,
            validAuthored.RootElement,
            new WorkflowDefinitionRegistrationOptions(At, WorkflowDefinitionStatus.Published));

        using var invalidAuthored = WorkflowAuthored("health-workflow", "2.0.0", "tax.roles/missing");
        var invalid = WorkflowDefinitionWireMapper.ToModel(
            invalidAuthored.RootElement, Tenant.Value, "health-workflow", "2.0.0");
        await workflows.RegisterAsync(
            invalid,
            invalidAuthored.RootElement,
            new WorkflowDefinitionRegistrationOptions(At, WorkflowDefinitionStatus.Published));

        var restarted = new RoleGateAdmission(AdmissionVocabulary(), workflows: workflows);
        var finding = Assert.Single(await restarted.InspectActiveAsync());
        Assert.Equal("workflow", finding.DefinitionKind);
        Assert.Equal("action:notify", finding.Gate);

        await workflows.WithdrawAsync(new DefinitionCoordinates(Tenant, "health-workflow", "2.0.0"));

        Assert.Empty(await new RoleGateAdmission(AdmissionVocabulary(), workflows: workflows).InspectActiveAsync());
    }

    [Fact]
    public void HostCompositionResolvesOneRoleGateForBothLifecycles()
    {
        var services = new ServiceCollection();
        services.AddAccessGrantModule();
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<RoleGateAdmission>(),
            provider.GetRequiredService<IRoleGateAdmission>());
        provider.ValidateRoleGateAdmissionComposition();
    }

    [Fact]
    public void ModuleAlwaysAddsTheConcreteAdmissionMappingAfterAPreRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRoleGateAdmission, PermissiveAdmission>();

        services.AddAccessGrantModule();

        Assert.Equal(2, services.Count(item => item.ServiceType == typeof(IRoleGateAdmission)));
        Assert.Contains(services, item =>
            item.ServiceType == typeof(IRoleGateAdmission)
            && item.ImplementationFactory is not null
            && item.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task RealHostCompositionRefusesAPreRegisteredPermissiveAdmission()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"s218-hostile-role-gate-{Guid.NewGuid():N}");
        try
        {
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    ["--LocalNode:RootSeedHex=" + new string('1', 64)],
                    sessionTokenOverride: "s218-composition-token",
                    dataDirectory: dataDirectory,
                    finalServiceRegistration: services =>
                        services.AddSingleton<IRoleGateAdmission, PermissiveAdmission>(),
                    installFootprintRootOverride: dataDirectory));

            Assert.Contains("authorization.role_gate.composition_invalid", refusal.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static IRoleVocabularyReader AdmissionVocabulary() => new InMemoryRoleVocabulary([
        RoleDefinition.CreatePackageRole(RoleDefinitionId.New(), "vendor-a-operator", "Vendor A", "vendor-a"),
        RoleDefinition.CreatePackageRole(RoleDefinitionId.New(), "vendor-b-operator", "Vendor B", "vendor-b"),
        RoleDefinition.CreateTenantRole(RoleDefinitionId.New(), "tenant-operator", "Tenant", Tenant),
    ]);

    private sealed class MutableRoleVocabulary(IRoleVocabularyReader inner) : IRoleVocabularyReader
    {
        private readonly HashSet<RoleReference> removed = [];

        public void Remove(RoleReference role) => removed.Add(role);

        public async ValueTask<RoleDefinition?> ResolveAsync(
            RoleReference role,
            CancellationToken ct = default) => removed.Contains(role)
                ? null
                : await inner.ResolveAsync(role, ct);

        public async ValueTask<IReadOnlyList<RoleDefinition>> ListAsync(CancellationToken ct = default) =>
            (await inner.ListAsync(ct)).Where(item => !removed.Contains(item.Role)).ToArray();
    }

    private static RoleGateAdmission Admission() => new(AdmissionVocabulary());

    private static FormDefinition Definition(string id, CascadeLayer layer, string subject)
    {
        var roles = subject.StartsWith("standing:", StringComparison.Ordinal) ? Array.Empty<string>() : new[] { subject };
        var standings = subject.StartsWith("standing:", StringComparison.Ordinal)
            ? new[] { new RecordStandingReference(subject["standing:".Length..]) }
            : null;
        return new FormDefinition(
            new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                new FormDefinitionId(id), new SemanticVersion(1, 0, 0), Tenant, layer,
                new FormDefinitionProvenance(IdentityRef.System, null), []),
            FormDefinitionStatus.Draft,
            new SchemaId("schema"),
            new HarborlineOverlay(
                new Dictionary<string, FieldOverlay>(),
                [new FormSection("main", InternationalizedText.FromInvariant("Main"), [], new SectionAccess(roles, roles, ReadStandings: standings))],
                []),
            At,
            At)
        {
            PackSource = layer == CascadeLayer.Pack ? new PackProjectionSource("vendor-a", "1.0.0") : null,
        };
    }

    private static WorkflowDefinition OrderedWorkflow(string id, string version, bool pack) => new()
    {
        Envelope = new DefinitionEnvelope<
            WorkflowDefinitionKey,
            WorkflowDefinitionVersion,
            TenantId,
            WorkflowDefinitionProvenance>(
                new WorkflowDefinitionKey(id),
                WorkflowDefinitionVersion.Parse(version),
                Tenant,
                pack ? CascadeLayer.Pack : CascadeLayer.Tenant,
                WorkflowDefinitionProvenance.Unspecified,
                []),
        Status = WorkflowDefinitionStatus.Draft,
        InitialState = "start",
        PackSource = pack ? new PackProjectionSource("vendor-a", "1.0.0") : null,
    };

    private static WorkflowDefinition CopyPackSource(
        WorkflowDefinition model,
        PackProjectionSource source) => new()
    {
        Envelope = model.Envelope,
        Status = model.Status,
        SubjectFormRef = model.SubjectFormRef,
        Mutability = model.Mutability,
        InitialState = model.InitialState,
        States = model.States,
        Transitions = model.Transitions,
        Triggers = model.Triggers,
        Actions = model.Actions,
        GuardRuleIds = model.GuardRuleIds,
        PackSource = source,
    };

    private static async Task<PackSeedProjectionSummary> ProjectThroughRealPackProjector(
        string id,
        string subject,
        IFormDefinitionStore store,
        AuthorizedFormDefinitionLifecycle lifecycle,
        IRoleGateAdmission admission)
    {
        Assert.Same(admission, lifecycle.RoleGateAdmission);
        var roles = subject.StartsWith("standing:", StringComparison.Ordinal)
            ? Array.Empty<string>()
            : new[] { subject };
        var standings = subject.StartsWith("standing:", StringComparison.Ordinal)
            ? new[] { subject["standing:".Length..] }
            : Array.Empty<string>();
        var content = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new
                    {
                        label = new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = "Name" } },
                        controlHint = "text",
                        piiSensitivity = "None",
                    },
                },
                sections = new[]
                {
                    new
                    {
                        id = "main",
                        title = new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = "Main" } },
                        fields = new[] { "name" },
                        access = new
                        {
                            readRoles = roles,
                            writeRoles = roles,
                            readStandings = standings,
                            writeStandings = standings,
                        },
                    },
                },
                rules = Array.Empty<object>(),
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["name"] = new { type = "text", required = false, options = (string[]?)null },
            },
        };
        var json = JsonSerializer.Serialize(content);
        var seed = new PackSeedItem(
            id, PackContentKind.FormDefinition, "1.0.0", json,
            Cid.FromBytes(Encoding.UTF8.GetBytes(json)));
        var installStore = new InMemoryPackInstallStore();
        var installed = new InstalledPack(
            "vendor-a", "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft,
            [seed], new Dictionary<string, int>(), At,
            PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        installStore.Commit(new PackInstallTransaction(
            Tenant, installed, new PackInstallWatermark("vendor-a", "1.0.0", new Dictionary<string, int>()), []));
        installStore.Activate(Tenant, "vendor-a", "1.0.0");
        var projector = new PackSeedProjector(
            installStore,
            Substitute.For<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            forms: store,
            schemas: new InMemorySchemaRegistry(TimeProvider.System),
            time: TimeProvider.System,
            authorizedForms: lifecycle);

        return await projector.ProjectActivePacksAsync(await PackAuthority());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PutThroughRealFormRoute(
        string id,
        string subject,
        AuthorizedFormDefinitionLifecycle lifecycle,
        IRoleGateAdmission admission)
    {
        Assert.Same(admission, lifecycle.RoleGateAdmission);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var requestAuthorization = Substitute.For<IAuthorizationContext>();
        requestAuthorization.HasPermission(Arg.Any<string>()).Returns(true);
        builder.Services.AddSingleton(requestAuthorization);
        // Ticket 205 slice 4: FormDefinitionRoutes resolves forms:author at the gate. This matrix is about
        // ROLE-GATE admission, not about the caller's holdings, so the gate holds everything and the
        // admission refusal remains the only thing that can move the verdict.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(TestRouteGate.AllowAll());
        var app = builder.Build();
        var activeTeam = new MatrixActiveTeamAccessor(new TeamContext(
            new TeamId(Guid.Parse("21800000-0000-0000-0000-000000000001")),
            "Matrix",
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System));
        FormDefinitionRoutes.Map(
            app, lifecycle, new InMemorySchemaRegistry(TimeProvider.System), activeTeam, TimeProvider.System);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var body = new
            {
                overlay = new
                {
                    fields = new Dictionary<string, object>
                    {
                        ["name"] = new
                        {
                            label = new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = "Name" } },
                            controlHint = "text",
                            piiSensitivity = "None",
                        },
                    },
                    sections = new[]
                    {
                        new
                        {
                            id = "main",
                            title = new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = "Main" } },
                            fields = new[] { "name" },
                            access = new
                            {
                                readRoles = new[] { subject },
                                writeRoles = new[] { subject },
                                readStandings = Array.Empty<string>(),
                                writeStandings = Array.Empty<string>(),
                            },
                        },
                    },
                    rules = Array.Empty<object>(),
                },
                fieldsMeta = new Dictionary<string, object>
                {
                    ["name"] = new { type = "text", required = false, options = (string[]?)null },
                },
            };
            using var response = await client.PutAsJsonAsync($"{FormDefinitionRoutes.RouteBase}/{id}", body);
            var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (response.StatusCode, responseBody);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static AuthorizationWriteContext Authority() => new(new ActorId("operator"), Tenant, At);

    private static async ValueTask<AuthorizedFormDefinitionLifecycle.WriteAuthority> PlatformFormAuthority(
        AuthorizedFormDefinitionLifecycle lifecycle,
        string definitionId)
    {
        var authority = new AuthorizationWriteContext(
            new ActorId("installer:authorization-definition-seed"), Tenant, At);
        var bootstrap = PlatformBootstrapDecision.Mint(AuthorizationDecision.CreateBootstrap(
            authority.Request(
                AuthorizationOperation.Parse(Permission.FormsAuthor),
                "forms",
                definitionId),
            "test-platform-form-seed"));
        return await lifecycle.DecidePlatformSeedAsync(definitionId, bootstrap);
    }

    private static async Task<PackProjectionAuthority> PackAuthority()
    {
        var scope = ScopeExpression.Parse("/records/vendor-a");
        var decision = await TestAuthorization.AllowGate().DecideAsync(new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
            new ActorId("operator"), Tenant, new AuthorizationTarget("pack", "vendor-a", scope), At));
        return new PackProjectionAuthority(decision, "vendor-a", "1.0.0", Tenant, new ActorId("operator"), At);
    }

    private static JsonDocument WorkflowAuthored(string id, string version, string actionRole) => JsonDocument.Parse($$"""
        {
          "key": "{{id}}",
          "version": "{{version}}",
          "tenant": "{{Tenant.Value}}",
          "status": "Published",
          "initialState": "start",
          "states": [
            { "id": "start", "kind": "Normal" },
            { "id": "done", "kind": "Terminal" }
          ],
          "triggers": [ { "id": "submit", "kind": "HumanAction", "task": "submit" } ],
          "transitions": [
            { "id": "submit", "from": "start", "on": "submit", "to": "done",
              "requiredRoles": ["sys.platform-roles/administrator"] }
          ],
          "actions": [
            { "id": "notify", "on": { "transition": "submit" }, "kind": "Notify",
              "capabilityRef": "notify.email", "classification": "AP", "requiredRoles": ["{{actionRole}}"] }
          ]
        }
        """);

    private static JsonDocument OrderedWorkflowAuthored(string id, string version, bool pack) =>
        JsonDocument.Parse($$"""
        {
          "key": "{{id}}", "version": "{{version}}", "tenant": "{{Tenant.Value}}",
          "initialState": "start", "states": [], "transitions": [], "triggers": [], "actions": [],
          "provenance": "{{(pack ? "Pack" : "Tenant")}}",
          "owner": { "scheme": "{{(pack ? "system" : "actor")}}", "value": "{{(pack ? "__sunfish" : "operator")}}" }
        }
        """);

    private static async Task<IReadOnlyList<FormDefinition>> Definitions(IFormDefinitionStore store)
    {
        var definitions = new List<FormDefinition>();
        await foreach (var definition in store.ListByTenantAsync(Tenant)) definitions.Add(definition);
        return definitions;
    }

    private static async Task<IReadOnlyList<WorkflowDefinitionRecord>> WorkflowDefinitions(IWorkflowDefinitionStore store)
    {
        var definitions = new List<WorkflowDefinitionRecord>();
        await foreach (var definition in store.ListByTenantAsync(Tenant)) definitions.Add(definition);
        return definitions;
    }

    private sealed class PermissiveAdmission : IRoleGateAdmission
    {
        public ValueTask AdmitAsync(RoleGatedDefinition definition, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<RoleGateFinding>> InspectActiveAsync(CancellationToken ct = default) =>
            new(Array.Empty<RoleGateFinding>());
    }

    private sealed class OrderedAdmission(List<string> order) : IRoleGateAdmission
    {
        public ValueTask AdmitAsync(RoleGatedDefinition definition, CancellationToken ct = default)
        {
            order.Add("admission");
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<RoleGateFinding>> InspectActiveAsync(CancellationToken ct = default) =>
            new(Array.Empty<RoleGateFinding>());
    }

    private sealed class OrderedLegalHold(List<string> order) : IFormDefinitionLegalHoldValidator
    {
        public ValueTask RefuseHeldAsync(
            FormDefinition definition,
            DefinitionLegalHoldOperation operation,
            CancellationToken cancellationToken = default)
        {
            order.Add("legal-hold");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OrderedEntityStore(IEntityMutationStore inner, List<string> order) : IEntityMutationStore
    {
        public Task<Entity?> GetAsync(EntityId id, VersionSelector version = default, CancellationToken ct = default) =>
            inner.GetAsync(id, version, ct);

        public Task<EntityId> CreateAsync(
            SchemaId schema, JsonDocument body, CreateOptions options, CancellationToken ct = default)
        {
            order.Add("writer");
            return inner.CreateAsync(schema, body, options, ct);
        }

        public Task<IReadOnlyList<EntityId>> CreateBatchAsync(
            IEnumerable<EntityDraft> drafts, CancellationToken ct = default)
        {
            order.Add("writer");
            return inner.CreateBatchAsync(drafts, ct);
        }

        public Task<VersionId> UpdateAsync(
            EntityId id, JsonDocument body, UpdateOptions options, CancellationToken ct = default)
        {
            order.Add("writer");
            return inner.UpdateAsync(id, body, options, ct);
        }

        public Task DeleteAsync(EntityId id, DeleteOptions options, CancellationToken ct = default)
        {
            order.Add("writer");
            return inner.DeleteAsync(id, options, ct);
        }

        public IAsyncEnumerable<Entity> QueryAsync(EntityQuery query, CancellationToken ct = default) =>
            inner.QueryAsync(query, ct);
    }

    private sealed class MatrixActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; private set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct = default) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void KeepEvent() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, Active));
    }

}
