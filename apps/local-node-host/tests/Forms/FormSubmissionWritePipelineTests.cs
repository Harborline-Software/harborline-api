using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// ck-10 S3 (DES-0029, ADR 0038): the form submission record runs the six stages through
/// <see cref="WritePipeline.RunAsync"/> under the decision the form engine carried.
/// </summary>
public sealed class FormSubmissionWritePipelineTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("form-submit-pipeline");
    private static readonly ActorId Submitter = new("form-submitter");
    private static readonly FormDefinitionId Form = new("intake");
    private static readonly SchemaId Schema = new("forms.intake");

    // ADR 0038's order, written out here rather than read from WritePipeline.Order.
    private static readonly WritePipelineStage[] SixStages =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    ];

    [Fact(DisplayName = "ck-10 S3: a form submission runs the six stages and stores the record")]
    public async Task Submission_RunsTheSixStages()
    {
        var h = new Harness();
        using var body = JsonDocument.Parse("""{"name":"submitted"}""");

        var id = await h.Writer.CreateAsync(Form, Schema, body, Options("first"), Decision("intake"));

        Assert.Equal(SixStages, h.Stages);
        Assert.Equal(new EntityId("forminstance", "forms", "first"), id);
        Assert.Equal("submitted", (await h.Entities.GetAsync(id))!.Body.RootElement.GetProperty("name").GetString());
    }

    [Fact(DisplayName = "ck-10 S3: a decision for another form stops the submission at authorize and stores nothing")]
    public async Task Submission_DecisionForAnotherForm_StopsAtAuthorize()
    {
        var h = new Harness();
        using var body = JsonDocument.Parse("""{"name":"submitted"}""");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Writer.CreateAsync(Form, Schema, body, Options("first"), Decision("another-form")));

        Assert.Equal([WritePipelineStage.Authorize], h.Stages);
        Assert.Null(await h.Entities.GetAsync(new EntityId("forminstance", "forms", "first")));
    }

    private static CreateOptions Options(string localPart) =>
        new("forminstance", "forms", localPart, Submitter, Tenant, At, ExplicitLocalPart: localPart);

    private static Harborline.Api.Foundation.Authorization.AuthorizationDecision Decision(string formId) =>
        TestAuthorization.AllowedDecision(Tenant, formId, "forms", Permission.FormsAuthor, Submitter.Value, At);

    private sealed class Harness : IWritePipelineObserver
    {
        public Harness()
        {
            Entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
            Writer = new AuthorizedFormEntityWriter(Entities, this);
        }

        public InMemoryEntityStore Entities { get; }
        public AuthorizedFormEntityWriter Writer { get; }
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);
    }
}
