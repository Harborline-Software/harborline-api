using System.Text.Json;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Engine.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed class CatalogueFieldSourceSaveTests
{
    private static readonly TenantId Tenant = new("catalogue-save");
    private static readonly ActorId Actor = new("catalogue-author");
    private static readonly FormDefinitionId Form = new("platform.detail.form");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opted_in_save_refuses_before_reuse_validation_or_mutation(bool receipt)
    {
        var reuse = Substitute.For<IReuseResolver>();
        var writes = Substitute.For<IAuthorizedFormEntityWriter>();
        await using var services = await ServicesAsync(true, collection =>
        {
            collection.AddSingleton(reuse);
            collection.AddSingleton(writes);
        });
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await TokenAsync(services);
        var before = await services.GetRequiredService<IFormDefinitionStore>().GetCurrentPublishedAsync(new(Tenant, Form.Value));
        using var candidate = JsonDocument.Parse("{\"title\":\"caller-replacement\"}");

        var failure = await Assert.ThrowsAsync<FormValidationException>(async () =>
        {
            if (receipt) await engine.SaveWithReceiptAsync(Form, candidate, token, TestAuthorization.FormWrite(token, Form, TestAuthorization.At));
            else await engine.SaveAsync(Form, candidate, token, CancellationToken.None);
        });
        var error = Assert.Single(failure.Result.Errors);
        Assert.Equal("catalogue-field-source.read-only", error.Code);
        Assert.Equal(ValidationErrorKind.Schema, error.Kind);
        Assert.Equal(string.Empty, error.JsonPointer);
        Assert.DoesNotContain("caller-replacement", error.Message, StringComparison.Ordinal);
        var validation = await engine.ValidateAsync(Form, candidate, token, CancellationToken.None);
        Assert.Equal(error, Assert.Single(validation.Errors));
        Assert.Empty(reuse.ReceivedCalls());
        Assert.Empty(writes.ReceivedCalls());
        Assert.Equal(CanonicalJson.Serialize(before), CanonicalJson.Serialize(
            await services.GetRequiredService<IFormDefinitionStore>().GetCurrentPublishedAsync(new(Tenant, Form.Value))));
    }

    [Fact]
    public async Task Legacy_form_still_validates_and_saves_through_the_composed_engine()
    {
        await using var services = await ServicesAsync(false);
        var engine = services.GetRequiredService<IFormEngine>();
        var token = await TokenAsync(services);
        using var candidate = JsonDocument.Parse("{}");
        Assert.True((await engine.ValidateAsync(Form, candidate, token, CancellationToken.None)).IsValid);
        var id = await engine.SaveAsync(Form, candidate, token, CancellationToken.None);
        Assert.NotNull(await services.GetRequiredService<IEntityStore>().GetAsync(id));
    }

    private static async Task<ServiceProvider> ServicesAsync(bool optedIn, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IFieldEncryptor>());
        services.AddSingleton(Substitute.For<IAuditLog>());
        services.AddTestAuthorizationGate();
        services.AddTestNodeForms();
        services.AddFrozenKernelClock(new FixedTime());
        services.AddSingleton(new FormEngineOptions());
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        var schema = await provider.GetRequiredService<ISchemaRegistry>().RegisterAsync("{\"type\":\"object\"}");
        var definitions = provider.GetRequiredService<IFormDefinitionStore>();
        var definition = new FormDefinition(Form, new(1, 0, 0), FormDefinitionStatus.Draft, Tenant,
            IdentityRef.System, schema.Id, HarborlineOverlay.Empty, null, TestAuthorization.At, TestAuthorization.At)
        {
            CatalogueFieldSource = optedIn ? JsonSerializer.Deserialize<CatalogueFieldSource>(CatalogueFieldSourceContractTests.Declaration) : null,
        };
        await definitions.RegisterAsync(definition);
        await definitions.PublishAsync(new(Tenant, Form.Value, "1.0.0"));
        return provider;
    }

    private static async Task<CapabilityToken> TokenAsync(IServiceProvider services)
    {
        var bearer = await services.GetRequiredService<IFormCapabilityIssuer>().IssueAsync(Tenant, Actor, [],
            [FormCapabilityAction.Read, FormCapabilityAction.Write], TestAuthorization.At.AddHours(1));
        return await services.GetRequiredService<IFormCapabilityVerifier>().VerifyAsync(bearer, TestAuthorization.At);
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TestAuthorization.At;
    }
}
