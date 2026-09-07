using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.SubmissionBinding;

public sealed class SubmissionBindingPersistenceTests
{
    private static readonly DateTimeOffset SubmittedAt = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-binding-test");
    private static readonly ActorId Actor = new("binding-test-actor");
    private static readonly FormDefinitionId FormId = new("binding.ticket.016");
    private static readonly SemanticVersion Version = new(1, 2, 3);

    [Fact]
    public async Task Audit_fault_after_entity_create_leaves_entity_and_complete_binding_committed_together()
    {
        var context = await CreateServicesAsync(new ThrowingAuditLog());
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var entities = services.GetRequiredService<IEntityStore>();
        var token = await IssueReadWriteTokenAsync(services);

        using var candidate = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<InjectedAuditFaultException>(() =>
            engine.SaveWithReceiptAsync(FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None));

        var persisted = new List<Entity>();
        await foreach (var entity in entities.QueryAsync(new EntityQuery(Schema: context.Schema.Id)))
        {
            persisted.Add(entity);
        }

        var submission = Assert.Single(persisted);
        var binding = Assert.IsType<EntityBinding>(submission.Binding);
        Assert.Equal(context.Schema.Id, binding.SchemaRef);
        Assert.Equal("binding.ticket.016", binding.DefinitionId);
        Assert.Equal("1.2.3", binding.DefinitionVersion);
        Assert.Equal("shipyard-jsonlogic/v1", binding.EngineVersion);
        Assert.Equal(new[] { "fr-CA", "en" }, binding.LocaleChain);
        Assert.Equal(SubmittedAt, binding.SubmittedAt);
    }

    [Fact]
    public async Task Submission_written_under_N_resolves_under_N_after_N_plus_one_is_published()
    {
        var context = await CreateServicesAsync(new SucceedingAuditLog());
        await using var services = context.Services;
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await IssueReadWriteTokenAsync(services);

        using var candidate = JsonDocument.Parse("{}");
        var receipt = await engine.SaveWithReceiptAsync(
            FormId, candidate, token, TestAuthorization.FormWrite(token, FormId, SubmittedAt), CancellationToken.None);

        var nextVersion = new SemanticVersion(2, 0, 0);
        var definitions = services.GetRequiredService<IFormDefinitionStore>();
        await definitions.RegisterAsync(Definition(context.Schema.Id, nextVersion));
        await definitions.PublishAsync(new DefinitionCoordinates(Tenant, FormId.Value, nextVersion.ToString()));

        var rendered = await engine.RenderAsync(
            FormId, receipt.InstanceId, token, CancellationToken.None);

        Assert.Equal(Version, rendered.Version);
    }

    private static async Task<TestContext> CreateServicesAsync(IAuditLog audit)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFieldEncryptor, UnusedFieldEncryptor>();
        services.AddTestAuthorizationGate();
        var keys = KeyPair.Generate();
        services.AddSingleton<IOperationSigner>(new Ed25519Signer(keys));
        services.AddSingleton<Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail>(audit is ThrowingAuditLog
            ? new ThrowingAuthorizedAuditTrail()
            : new Harborline.Api.Kernel.Audit.InMemoryAuditTrail());
        services.AddTestNodeForms();
        services.AddSingleton(audit);
        services.AddFrozenKernelClock(new FixedTimeProvider(SubmittedAt));
        services.AddSingleton(new FormEngineOptions
        {
            RequireGovernanceEnforcement = true,
            LocaleChain = new[] { "fr-CA", "en" },
        });

        var provider = services.BuildServiceProvider();
        var schema = await provider.GetRequiredService<ISchemaRegistry>().RegisterAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false
            }
            """);
        var definitions = provider.GetRequiredService<IFormDefinitionStore>();
        var definition = Definition(schema.Id, Version);
        await definitions.RegisterAsync(definition);
        await definitions.PublishAsync(new DefinitionCoordinates(Tenant, FormId.Value, Version.ToString()));
        return new TestContext(provider, schema);
    }

    private sealed class ThrowingAuthorizedAuditTrail : Harborline.Api.Kernel.Audit.IAuthorizedAuditTrail
    {
        public ValueTask AppendAuthorizedAsync(
            Harborline.Api.Kernel.Audit.AuditRecord record,
            Harborline.Api.Foundation.Authorization.AuthorizationDecision decision,
            CancellationToken ct = default,
            SeparationOfDutyDecision? approval = null) =>
            ValueTask.FromException(new InjectedAuditFaultException());

        public ValueTask AppendAsync(Harborline.Api.Kernel.Audit.AuditRecord record, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<Harborline.Api.Kernel.Audit.AuditRecord> QueryAsync(
            Harborline.Api.Kernel.Audit.AuditQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static FormDefinition Definition(SchemaId schemaRef, SemanticVersion version) => new(
        Id: FormId,
        Version: version,
        Status: FormDefinitionStatus.Draft,
        Tenant: Tenant,
        Owner: IdentityRef.System,
        SchemaRef: schemaRef,
        Overlay: HarborlineOverlay.Empty,
        Lineage: null,
        CreatedAt: SubmittedAt,
        UpdatedAt: SubmittedAt);

    private static async Task<CapabilityToken> IssueReadWriteTokenAsync(IServiceProvider services)
    {
        var bearer = await services.GetRequiredService<IFormCapabilityIssuer>().IssueAsync(
            Tenant,
            Actor,
            Array.Empty<string>(),
            new[] { FormCapabilityAction.Read, FormCapabilityAction.Write },
            SubmittedAt.AddHours(1));
        return await services.GetRequiredService<IFormCapabilityVerifier>().VerifyAsync(bearer, SubmittedAt);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record TestContext(ServiceProvider Services, Schema Schema);

    private sealed class UnusedFieldEncryptor : IFieldEncryptor
    {
        public Task<EncryptedField> EncryptAsync(ReadOnlyMemory<byte> plaintext, TenantId tenant, CancellationToken ct) =>
            throw new InvalidOperationException("The empty candidate has no fields to encrypt.");
    }

    private sealed class InjectedAuditFaultException : Exception;

    private sealed class ThrowingAuditLog : IAuditLog
    {
        public Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default) =>
            throw new InjectedAuditFaultException();

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> VerifyChainAsync(EntityId entity, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class SucceedingAuditLog : IAuditLog
    {
        public Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default) =>
            Task.FromResult(new AuditId(1));

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> VerifyChainAsync(EntityId entity, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
