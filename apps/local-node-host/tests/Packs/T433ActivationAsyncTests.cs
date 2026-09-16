using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class T433ActivationAsyncTests
{
    [Fact]
    public async Task Activation_awaits_observers_outside_the_lease_and_preserves_committed_success_on_failure()
    {
        var (installer, store, context, projector) = Fixture();
        using var cancellation = new CancellationTokenSource();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        projector.Reaction = async () =>
        {
            observed.SetResult();
            await release.Task;
            throw new IOException("post-commit observer failure");
        };

        var activation = installer.ActivateAsync(context, "test.async", "1.0.0", cancellation.Token);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(activation.IsCompleted);
        try
        {
            // An independent reader must see the committed pointer while notification is still pending.
            var published = await Task.Run(() => store.GetActive(context.Tenant, "test.async"))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("1.0.0", published!.Version);
            cancellation.Cancel(); // After commit, cancellation must not manufacture a rollback.
        }
        finally { release.SetResult(); }

        var outcome = await activation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(outcome.Activated);
        Assert.True(outcome.Projected);
        Assert.Contains("post-commit observer failure", outcome.Detail);
        Assert.Empty(((IPackProjectionAdmissionStore)store).ListIncompleteProjectionAdmissions());
    }

    [Fact]
    public async Task Canceled_activation_leaves_the_draft_and_admissions_unchanged()
    {
        var (installer, store, context, projector) = Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.ActivateAsync(context, "test.async", "1.0.0", cancellation.Token));
        Assert.Null(store.GetActive(context.Tenant, "test.async"));
        Assert.Equal(PackLifecycleState.Draft, store.GetVersion(context.Tenant, "test.async", "1.0.0")!.Lifecycle);
        Assert.Empty(((IPackProjectionAdmissionStore)store).ListIncompleteProjectionAdmissions());
        Assert.Equal(0, projector.Projections);
    }

    private static (PackInstaller, InMemoryPackInstallStore, PackInstallContext, ObservingProjector) Fixture()
    {
        var tenant = new TenantId("t433-async");
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryPackInstallStore();
        var pack = new InstalledPack("test.async", "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft,
            [], new Dictionary<string, int>(), now, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]),
            1, TrustScope.OwnRoster, []);
        store.Commit(new(tenant, pack, new(pack.PackKey, pack.Version, new Dictionary<string, int>()), []));
        var projector = new ObservingProjector();
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), store,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(),
            Authorization.TestAuthorization.AllowGate(), projector);
        var context = new PackInstallContext(tenant, Substitute.For<IPackTrustStore>(),
            Substitute.For<IPackRevocationList>(), now, TimeSpan.FromHours(1), Principal: "operator");
        return (installer, store, context, projector);
    }

    private sealed class ObservingProjector : IPackProjectionDispatcher
    {
        internal Func<Task> Reaction = () => Task.CompletedTask;
        internal int Projections;
        public void StageProjection(PackProjectionTransaction transaction) => transaction.AfterCommit(() => Reaction());
        public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Projections++;
            return null;
        }
    }
}
