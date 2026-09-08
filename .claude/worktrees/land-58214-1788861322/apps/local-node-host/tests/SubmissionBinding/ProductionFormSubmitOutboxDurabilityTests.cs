using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;

namespace Harborline.Api.LocalNodeHost.Tests.SubmissionBinding;

/// <summary>
/// Production-composition proof for ticket 016 acceptance 3: the shipped node must both boot with a
/// non-volatile outbox and preserve an enqueued projection intent across a complete host restart.
/// </summary>
[Collection("Harborline process environment")]
public sealed class ProductionFormSubmitOutboxDurabilityTests
{
    [Fact(DisplayName = "ticket 016: production host boots and form-submit outbox survives restart")]
    public async Task ProductionHost_OutboxEntry_SurvivesStoreRoundTrip()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-form-submit-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        var previousDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var previousWebClientEnabled = Environment.GetEnvironmentVariable("LocalNode__WebClient__Enabled");
        var started = false;

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable(
                "LocalNode__RootSeedHex",
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", "false");

            await LocalNodeHostRuntime.StartAsync(
                "ticket-016-outbox-token",
                dataDirectory,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            started = true;

            var firstProvider = Assert.IsAssignableFrom<IServiceProvider>(LocalNodeHostRuntime.CurrentServices);
            var firstOutbox = firstProvider.GetRequiredService<IFormSubmitOutbox>();
            Assert.IsNotType<InMemoryFormSubmitOutbox>(firstOutbox);

            var submittedAt = new DateTimeOffset(2026, 8, 18, 12, 34, 56, TimeSpan.Zero);
            var instanceId = new EntityId("form-instance", "ticket-016", Guid.NewGuid().ToString("N"));
            using var submittedValues = JsonDocument.Parse("""{"condition":"safe","score":7}""");
            var entry = await firstOutbox.EnqueueAsync(new FormSubmitContext(
                Form: new FormDefinitionId("ticket-016/durable-outbox"),
                InstanceId: instanceId,
                Tenant: new TenantId("ticket-016-tenant"),
                Actor: new ActorId("ticket-016-actor"),
                SubmittedAt: submittedAt,
                SubmittedValues: submittedValues,
                CaseRef: "case-016"));

            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            started = false;

            await LocalNodeHostRuntime.StartAsync(
                "ticket-016-outbox-token",
                dataDirectory,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            started = true;

            var secondProvider = Assert.IsAssignableFrom<IServiceProvider>(LocalNodeHostRuntime.CurrentServices);
            var secondOutbox = secondProvider.GetRequiredService<IFormSubmitOutbox>();
            Assert.NotSame(firstOutbox, secondOutbox);

            var restored = await secondOutbox.GetAsync(entry.Id);
            Assert.NotNull(restored);
            Assert.Equal(entry.Id, restored.Id);
            Assert.Equal(entry.Form, restored.Form);
            Assert.Equal(entry.InstanceId, restored.InstanceId);
            Assert.Equal(entry.Tenant, restored.Tenant);
            Assert.Equal(entry.Actor, restored.Actor);
            Assert.Equal(entry.SubmittedAt, restored.SubmittedAt);
            Assert.Equal(entry.SubmittedValuesJson, restored.SubmittedValuesJson);
            Assert.Equal(entry.CaseRef, restored.CaseRef);
        }
        finally
        {
            if (started)
            {
                await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            }

            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspnetEnvironment);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", previousWebClientEnabled);

            // The production composition enables SQLite pooling. Release pooled handles after the
            // second host stops so Windows can remove the per-test data directory deterministically.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
