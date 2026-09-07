using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// Host composition for the enrollment compensating-control AUDIT (enrollment Phase C control #1; #1295 F1/F2;
/// <c>project_sod_compensating_controls</c>) — the wiring that makes the "second set of eyes" compensating
/// control LIVE rather than test-only. It registers a real <see cref="IEnrollmentCompensatingControlRecorder"/>
/// (<see cref="KernelAuditEnrollmentCompensatingControlRecorder"/>) bound to an immutable, signed, tamper-evident audit trail, so a runtime
/// admit/revoke/grant/transfer driven through the node's admission surface lands a enrollment control audit record in the
/// trail — not just a direct-call unit test (the F1 gap).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a SEPARATE InMemoryAuditTrail and NOT the node's EF financial-audit path.</b> The node's
/// system-of-record financial audit path is the recoverable EF <c>NodeAuditWriteEnlister</c> +
/// <c>NodeAuditEventReader</c> over <c>local-node.db</c> — deliberately wired to take NO kernel CRDT writer /
/// <c>IEventLog</c> (the SC4-T9(b) recoverability guard FORBIDS those types). The kernel-audit
/// <see cref="IAuditTrail"/> the <see cref="KernelAuditEnrollmentCompensatingControlRecorder"/> + <c>AuditExportService</c> consume is a
/// DIFFERENT substrate (it lives in <c>Harborline.Api.Kernel.Audit</c>, NOT the forbidden
/// <c>Harborline.Api.Kernel.Events</c>/<c>Ledger</c>). We register the in-memory variant here (NOT
/// <c>AddHarborlineKernelAudit</c>, which wires <c>EventLogBackedAuditTrail</c> over the FORBIDDEN
/// <c>IEventLog</c>): it gives the compensating control a signed, tamper-evident, undeletable, <c>audit:read</c>-gated
/// trail without disturbing the SC4-guarded financial path. The durable synced audit doctype replaces this
/// in-memory store behind the same <see cref="IAuditTrail"/> seam (a flagged follow-on); the seam + the live
/// wiring are what F1 needs.
/// </para>
/// <para>
/// <b>Singleton, not Scoped.</b> The recorder is closed over from the OUTER host container by the admission
/// surface (a fire-and-forget side-channel off a route handler), so its backing trail must OUTLIVE request
/// scopes — registered as a Singleton (the kernel-audit reader-in-memory extension is Scoped, which would give
/// each scope a fresh empty store). The reviewer-facing read-side shares the SAME singleton instance.
/// </para>
/// <para>
/// <b>Fail-safe-but-LOUD (#1295 F2).</b> The sink's <c>onFault</c> is wired here to a non-optional logger
/// callback: an append fault on a enrollment control op is signalled at WARN (degraded-mode) — and ESCALATED to Error for
/// <see cref="AuditEventType.OwnershipTransferred"/> (the root-grant moving, the highest-stakes op) — so an
/// unauditable enrollment control op is DETECTABLE rather than silently swallowed. The op itself still does not brick
/// (fail-safe); the unaudited event just stops being invisible.
/// </para>
/// </remarks>
public static class EnrollmentCompensatingControlAuditComposition
{
    /// <summary>The non-secret destination label the enrollment control audit trail tags its records' attribution with.</summary>
    public const string TrailLabel = "local-node-sod-audit";

    /// <summary>
    /// Registers the LIVE enrollment compensating-control audit: a singleton in-memory kernel audit
    /// <see cref="IAuditTrail"/> + <see cref="IAuditEventReader"/>, and <see cref="IEnrollmentCompensatingControlRecorder"/> bound to
    /// <see cref="KernelAuditEnrollmentCompensatingControlRecorder"/> over that trail + the node's <see cref="NodePrincipalSigner"/> (the
    /// canonical operation signer) with a fail-safe-but-LOUD <c>onFault</c>. The caller is responsible for
    /// having registered <see cref="NodePrincipalSigner"/> + <see cref="IOperationSigner"/> (Program.cs does).
    /// </summary>
    public static IServiceCollection AddEnrollmentCompensatingControlAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ticket 272 slice 1 — the ADR 0067 clause 2 decider. Stateless and clock-free, so a singleton; it
        // is registered here, behind the same host provider seam as the rest of the separation-of-duty
        // control, so writers resolve the ONE engine rather than deriving an approval fact themselves. No
        // writer consumes it yet (later slices convert them); the arch fence keeps that true meanwhile.
        services.TryAddSingleton<SeparationOfDutyEngine>();

        // The signed, tamper-evident, undeletable kernel audit trail the compensating control records into — a SINGLETON
        // in-memory store (NOT the Scoped reader-in-memory extension; the sink outlives request scopes). The
        // reader shares the SAME concrete instance so a reviewer reads exactly what was recorded.
        services.TryAddSingleton<InMemoryAuditTrail>();
        services.TryAddSingleton<IAuditTrail>(sp => sp.GetRequiredService<InMemoryAuditTrail>());
        services.TryAddSingleton<IAuthorizedAuditTrail>(sp => sp.GetRequiredService<InMemoryAuditTrail>());
        services.TryAddSingleton<IAuditEventReader>(sp => new InMemoryAuditEventReader(
            sp.GetRequiredService<InMemoryAuditTrail>(),
            sp.GetRequiredService<IAuditTrail>(),
            sp.GetRequiredService<IOperationSigner>()));

        // Replace the foundation NullEnrollmentCompensatingControlRecorder default with the LIVE kernel-audit-backed adapter — this is
        // the F1 fix: a real IEnrollmentCompensatingControlRecorder is now in the SHIPPING DI graph, so a runtime admit records LIVE.
        // Replace (not TryAdd): the foundation defaults may have already TryAdd'd the no-op; the shipping host
        // wins.
        services.Replace(ServiceDescriptor.Singleton<IEnrollmentCompensatingControlRecorder>(sp =>
        {
            var trail = sp.GetRequiredService<IAuditTrail>();
            // The node's canonical principal signer attributes the enrollment control audit envelope to the node principal.
            var signer = sp.GetRequiredService<NodePrincipalSigner>().Signer;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Harborline.Api.LocalNodeHost.Enrollment.CompensatingControlAudit");

            return new KernelAuditEnrollmentCompensatingControlRecorder(
                trail,
                signer,
                time: sp.GetRequiredService<TimeProvider>(),
                onFault: (eventType, ex) =>
                {
                    // #1295 F2 — fail-safe-but-LOUD. A enrollment control op proceeded but its audit record could NOT be
                    // written: signal it so it is detectable. OwnershipTransferred (the root-grant moving) is
                    // the highest-stakes op — escalate it to Error; the rest are WARN/degraded-mode.
                    if (eventType.Equals(AuditEventType.OwnershipTransferred))
                    {
                        logger.LogError(
                            ex,
                            "Enrollment compensating-control DEGRADED: the highest-stakes enrollment op {EventType} "
                            + "PROCEEDED but its audit record could NOT be written — the root-grant moved "
                            + "UNAUDITED. The op is not bricked (fail-safe), but the second-set-of-eyes record "
                            + "is missing. Investigate the audit trail immediately.",
                            eventType.Value);
                    }
                    else
                    {
                        logger.LogWarning(
                            ex,
                            "Enrollment compensating-control DEGRADED: enrollment op {EventType} PROCEEDED but its "
                            + "audit record could NOT be written — the change is UNAUDITED. The op is not "
                            + "bricked (fail-safe); the second-set-of-eyes record is missing.",
                            eventType.Value);
                    }
                });
        }));

        return services;
    }
}
