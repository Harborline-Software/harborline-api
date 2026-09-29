using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// Host composition for the kernel audit trail and the enrollment compensating-control AUDIT (enrollment
/// Phase C control #1; #1295 F1; <c>project_sod_compensating_controls</c>) — the wiring that makes the "second
/// set of eyes" compensating control LIVE rather than test-only. It registers a real
/// <see cref="IEnrollmentCompensatingControlRecorder"/> (<see cref="KernelAuditEnrollmentCompensatingControlRecorder"/>)
/// whose signed record commits with the enrollment change, and the durable trail the record is read from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The trail is durable (T-986).</b> <see cref="NodeAuditTrailStore"/> keeps every kernel audit record in
/// <c>local-node.db</c>, behind <see cref="AuthorityCapturingAuditTrail"/>, and the one
/// <see cref="IAuditEventReader"/> reads it. It is NOT <c>AddHarborlineKernelAudit</c>, whose
/// <c>EventLogBackedAuditTrail</c> needs the kernel <c>IEventLog</c> that the SC4-T9(b) recoverability guard
/// forbids on the node's financial path. The in-memory trail this composition shipped before lost every record
/// on restart.
/// </para>
/// <para>
/// <b>Singleton, not Scoped.</b> The trail is closed over from the OUTER host container by the admission surface
/// and the hosted web app, so it is one singleton; the reader and the writer share the same store.
/// </para>
/// </remarks>
public static class EnrollmentCompensatingControlAuditComposition
{
    /// <summary>The non-secret destination label the enrollment control audit trail tags its records' attribution with.</summary>
    public const string TrailLabel = "local-node-sod-audit";

    /// <summary>
    /// Registers the durable kernel audit trail (<see cref="IAuditTrail"/>, <see cref="IAuthorizedAuditTrail"/>,
    /// <see cref="ICapturedAuditTrail"/>, <see cref="IAuditEventReader"/>) over <see cref="NodeAuditTrailStore"/>,
    /// and <see cref="IEnrollmentCompensatingControlRecorder"/> bound to
    /// <see cref="KernelAuditEnrollmentCompensatingControlRecorder"/> over the node's <see cref="NodePrincipalSigner"/>.
    /// The caller registers <see cref="NodePrincipalSigner"/>, <see cref="IOperationSigner"/> and the
    /// <c>local-node.db</c> search context factory (Program.cs does).
    /// </summary>
    public static IServiceCollection AddEnrollmentCompensatingControlAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ticket 272 slice 1 — the ADR 0067 clause 2 decider. Stateless and clock-free, so a singleton; it
        // is registered here, behind the same host provider seam as the rest of the separation-of-duty
        // control, so writers resolve the ONE engine rather than deriving an approval fact themselves. No
        // writer consumes it yet (later slices convert them); the arch fence keeps that true meanwhile.
        services.TryAddSingleton<SeparationOfDutyEngine>();

        services.TryAddSingleton<NodeAuditTrailStore>();
        services.TryAddSingleton(sp => new AuthorityCapturingAuditTrail(sp.GetRequiredService<NodeAuditTrailStore>()));
        services.TryAddSingleton<IAuditTrail>(sp => sp.GetRequiredService<AuthorityCapturingAuditTrail>());
        services.TryAddSingleton<IAuthorizedAuditTrail>(sp => sp.GetRequiredService<AuthorityCapturingAuditTrail>());
        services.TryAddSingleton<ICapturedAuditTrail>(sp => sp.GetRequiredService<AuthorityCapturingAuditTrail>());
        services.TryAddSingleton<IAuditEventReader>(sp => new SnapshotAuditEventReader(
            sp.GetRequiredService<NodeAuditTrailStore>().SnapshotAsync,
            sp.GetRequiredService<IAuditTrail>(),
            sp.GetRequiredService<IOperationSigner>()));

        // Replace the foundation NullEnrollmentCompensatingControlRecorder default with the LIVE kernel-audit-backed
        // adapter. Replace (not TryAdd): the foundation defaults may have already TryAdd'd the no-op; the shipping
        // host wins. The node's canonical principal signer attributes the envelope to the node principal.
        services.Replace(ServiceDescriptor.Singleton<IEnrollmentCompensatingControlRecorder>(sp =>
            new KernelAuditEnrollmentCompensatingControlRecorder(
                sp.GetRequiredService<NodePrincipalSigner>().Signer,
                sp.GetRequiredService<TimeProvider>())));

        return services;
    }
}
