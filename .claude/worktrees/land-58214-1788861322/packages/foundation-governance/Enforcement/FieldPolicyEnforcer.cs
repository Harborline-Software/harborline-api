using System.Text.Json;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.MissionSpace.Regulatory;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.SecurityPolicy.Retention;
using Harborline.Api.Foundation.Governance.Bridges;
using Harborline.Api.Foundation.Governance.Consent;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;

namespace Harborline.Api.Foundation.Governance.Enforcement;

/// <summary>
/// The SPINE-2 PEP orchestrator (ADR 0140 D2 §4). Composes the BUILT encrypt / retain /
/// reside / audit / shred primitives in the fixed, fail-closed order each trigger requires.
/// It owns no persistence and no key material — it sequences injected interfaces and returns
/// the artifact (ciphertext / projection / verdict) the caller acts on inside its txn.
/// </summary>
public sealed class FieldPolicyEnforcer : IFieldPolicyEnforcer
{
    private readonly IFieldEncryptor _encryptor;
    private readonly ISubjectFieldEncryptor _subjectEncryptor;
    private readonly IRetentionPolicyResolver _retention;
    private readonly IDataResidencyEnforcer _residencyEnforcer;
    private readonly IResidencyEligibilityResolver _residency;
    private readonly IFieldClassAuditEventClassMap _classMap;
    private readonly IAuditLog _audit;
    private readonly ISubjectErasureService _erasure;
    private readonly ILegalHoldRegistry _holds;
    private readonly IConsentGate _consent;
    private readonly TimeProvider _time;

    /// <summary>Construct over the injected primitive interfaces + bridges.</summary>
    public FieldPolicyEnforcer(
        IFieldEncryptor encryptor,
        ISubjectFieldEncryptor subjectEncryptor,
        IRetentionPolicyResolver retention,
        IDataResidencyEnforcer residencyEnforcer,
        IResidencyEligibilityResolver residency,
        IFieldClassAuditEventClassMap classMap,
        IAuditLog audit,
        ISubjectErasureService erasure,
        ILegalHoldRegistry holds,
        IConsentGate consent,
        TimeProvider? time = null)
    {
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _subjectEncryptor = subjectEncryptor ?? throw new ArgumentNullException(nameof(subjectEncryptor));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _residencyEnforcer = residencyEnforcer ?? throw new ArgumentNullException(nameof(residencyEnforcer));
        _residency = residency ?? throw new ArgumentNullException(nameof(residency));
        _classMap = classMap ?? throw new ArgumentNullException(nameof(classMap));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _erasure = erasure ?? throw new ArgumentNullException(nameof(erasure));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<FieldStoreOutcome> StoreAsync(StoreFieldContext ctx, CancellationToken ct = default)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        var policy = ctx.Policy;
        var applied = new List<string>();

        // 1. Residency — eligible-set (fail-closed for a classified field) then the enforcer.
        var elig = _residency.Resolve(policy, Trigger.Store);
        if (elig.Required)
        {
            // Explicit prohibition wins over the allow-set (a jurisdiction in both is rejected).
            if (elig.ProhibitedJurisdictions.Contains(ctx.TargetJurisdiction, StringComparer.Ordinal))
            {
                throw new DataResidencyViolationException(policy.Field,
                    $"store target jurisdiction '{ctx.TargetJurisdiction}' is explicitly prohibited.");
            }
            if (!elig.AllowedJurisdictions.Contains(ctx.TargetJurisdiction, StringComparer.Ordinal))
            {
                throw new DataResidencyViolationException(policy.Field,
                    $"store target jurisdiction '{ctx.TargetJurisdiction}' is not in the allowed set " +
                    $"[{string.Join(", ", elig.AllowedJurisdictions)}].");
            }
            var verdict = await _residencyEnforcer
                .EnforceAsync(RecordClassOf(policy), ctx.TargetJurisdiction, ct).ConfigureAwait(false);
            if (!verdict.IsPermitted)
            {
                throw new DataResidencyViolationException(policy.Field,
                    verdict.Detail ?? "data-residency enforcer rejected the store.");
            }
            applied.Add("reside");
        }

        // 2. Encrypt (per-subject DEK when the effect is subject-scoped).
        EncryptedField? cipher = null;
        var enc = policy.Effect(EffectKind.Encrypt, Trigger.Store);
        if (enc is not null)
        {
            if (enc.Params.SubjectScoped)
            {
                if (ctx.Subject is not { } subj)
                {
                    throw new GovernanceConfigurationException(
                        $"field '{policy.Field}' has a subject-scoped encrypt effect but no subject was supplied.");
                }
                cipher = await _subjectEncryptor
                    .EncryptForSubjectAsync(ctx.Value, ctx.Tenant, subj, ct).ConfigureAwait(false);
            }
            else
            {
                cipher = await _encryptor.EncryptAsync(ctx.Value, ctx.Tenant, ct).ConfigureAwait(false);
            }
            applied.Add("encrypt");
        }

        // 3. Retention clock — class→AuditEventClass bridge (fail-closed on unmappable class).
        RetentionVerdict? retention = null;
        var ret = policy.Effect(EffectKind.Retain, Trigger.Store);
        if (ret is not null)
        {
            var auditClass = _classMap.Resolve(ret.Params.RetainFloorClass ?? string.Empty);
            retention = await _retention
                .ResolveAsync(ctx.Tenant, auditClass, ctx.RecordCreatedAt, ct).ConfigureAwait(false);
            applied.Add("retain");
        }

        // 4. Audit envelope.
        var audited = false;
        if (policy.Has(EffectKind.Audit, Trigger.Store))
        {
            await _audit.AppendAsync(BuildAppend(ctx.Entity, ctx.Actor, ctx.Tenant, Op.Write, policy.Field, "store"), ct)
                .ConfigureAwait(false);
            audited = true;
            applied.Add("audit");
        }

        return new FieldStoreOutcome(cipher is not null, cipher, retention, audited, applied);
    }

    /// <inheritdoc />
    public async Task<FieldReadProjection> ProjectForReadAsync(ReadFieldContext ctx, CancellationToken ct = default)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        var policy = ctx.Policy;

        // 1. Access — unauthorized ⇒ value withheld (the existing FormViewField posture).
        if (!IsAuthorized(policy.Aspect.ReadRoles, ctx.ActorRoles))
        {
            return new FieldReadProjection(Readable: false, Redacted: true, Masked: false, Value: null, Audited: false);
        }

        // 2. Consent — fail-closed when required and absent.
        await EnsureConsentAsync(policy, Trigger.Read, ctx.Tenant, ctx.Subject, ctx.Entity, ct).ConfigureAwait(false);

        // 3. Redact (omit) or Mask (partial reveal); redact wins if both are present.
        var (value, redacted, masked) = ProjectValue(policy, Trigger.Read, ctx.Value);

        // 4. Audit a sensitive read.
        var audited = false;
        if (policy.Has(EffectKind.Audit, Trigger.Read))
        {
            await _audit.AppendAsync(BuildAppend(ctx.Entity, ctx.Actor, ctx.Tenant, Op.Read, policy.Field, "read"), ct)
                .ConfigureAwait(false);
            audited = true;
        }

        return new FieldReadProjection(Readable: !redacted, redacted, masked, value, audited);
    }

    /// <inheritdoc />
    public async Task<FieldReadProjection> ProjectForExportAsync(ExportFieldContext ctx, CancellationToken ct = default)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        var policy = ctx.Policy;

        if (!IsAuthorized(policy.Aspect.ReadRoles, ctx.ActorRoles))
        {
            return new FieldReadProjection(Readable: false, Redacted: true, Masked: false, Value: null, Audited: false);
        }

        await EnsureConsentAsync(policy, Trigger.Export, ctx.Tenant, ctx.Subject, ctx.Entity, ct).ConfigureAwait(false);

        // Residency egress — may this classified value leave the region?
        var elig = _residency.Resolve(policy, Trigger.Export);
        if (elig.Required &&
            elig.ProhibitedJurisdictions.Contains(ctx.DestinationJurisdiction, StringComparer.Ordinal))
        {
            // Explicit prohibition wins over the allow-set (a jurisdiction in both is rejected).
            throw new DataResidencyViolationException(policy.Field,
                $"export destination '{ctx.DestinationJurisdiction}' is explicitly prohibited.");
        }
        if (elig.Required &&
            !elig.AllowedJurisdictions.Contains(ctx.DestinationJurisdiction, StringComparer.Ordinal))
        {
            throw new DataResidencyViolationException(policy.Field,
                $"export destination '{ctx.DestinationJurisdiction}' is not in the allowed set " +
                $"[{string.Join(", ", elig.AllowedJurisdictions)}].");
        }

        var (value, redacted, masked) = ProjectValue(policy, Trigger.Export, ctx.Value);

        // Export of CLASSIFIED data is ALWAYS audited (mandatory), regardless of an explicit effect.
        var audited = false;
        if (policy.Tags.Count > 0 || policy.Has(EffectKind.Audit, Trigger.Export))
        {
            await _audit.AppendAsync(BuildAppend(ctx.Entity, ctx.Actor, ctx.Tenant, Op.Read, policy.Field, "export"), ct)
                .ConfigureAwait(false);
            audited = true;
        }

        return new FieldReadProjection(Readable: !redacted, redacted, masked, value, audited);
    }

    /// <inheritdoc />
    public async Task<SubjectErasureOutcomeReport> EraseSubjectAsync(EraseSubjectContext ctx, CancellationToken ct = default)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        // 1a. Legal-hold gate (definitive) — a held subject is never shredded.
        if (_holds.IsHeld(ctx.Tenant, ctx.Subject))
        {
            return new SubjectErasureOutcomeReport(Erased: false, BlockedByHold: true,
                BlockReason: "an active legal hold blocks erasure.", ServiceOutcome: null);
        }

        // 1b. Retention-floor gate (the §3.3 lattice: legal-hold > retention-floor > erase).
        if (ctx.RetainedRecords is not null)
        {
            foreach (var rec in ctx.RetainedRecords)
            {
                var verdict = await _retention
                    .ResolveAsync(ctx.Tenant, rec.Class, rec.CreatedAt, ct).ConfigureAwait(false);
                if (verdict.MinimumHoldUntil > ctx.AsOf)
                {
                    return new SubjectErasureOutcomeReport(Erased: false, BlockedByHold: true,
                        BlockReason: $"a retention floor holds until {verdict.MinimumHoldUntil:O}.",
                        ServiceOutcome: null);
                }
            }
        }

        // 2. Shred — the BUILT service performs ≥2-approver + dwell + key-destroy + tombstone +
        //    audit + propagate. A propagator fault PROPAGATES out of EraseAsync (fail-loud).
        var result = await _erasure.EraseAsync(ctx.Request, ct).ConfigureAwait(false);
        return new SubjectErasureOutcomeReport(
            Erased: result.Outcome == SubjectErasureOutcome.Erased,
            BlockedByHold: false,
            BlockReason: null,
            ServiceOutcome: result.Outcome);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static bool IsAuthorized(IReadOnlyList<string>? requiredRoles, IReadOnlyCollection<string> actorRoles)
        => requiredRoles is null || requiredRoles.Any(r => actorRoles.Contains(r, StringComparer.Ordinal));

    /// <summary>
    /// The subject-consent gate at ITS point of use (ticket 213). The act is decided here, on the instant it
    /// happens and against the record the tenant has on file — never against a bit resolved earlier. The
    /// refusal carries the decision's reason so a caller records what it refused on.
    /// </summary>
    private async Task EnsureConsentAsync(
        ResolvedFieldPolicy policy, Trigger trigger, TenantId tenant, SubjectId? subject,
        EntityId entity, CancellationToken ct)
    {
        var consent = policy.Effect(EffectKind.Consent, trigger);
        if (consent is null) return;
        var purpose = consent.Params.ConsentPurpose ?? trigger.ToString().ToLowerInvariant();
        if (subject is not { } subj)
        {
            // No subject means there is nobody whose consent could be on file: refuse as absent.
            throw new ConsentRequiredException(policy.Field, purpose, ConsentRefusal.NoRecord);
        }

        var decision = await _consent.DecideAsync(
            new ConsentRequest(tenant, subj, purpose, ConsentScope(entity), _time.GetUtcNow()), ct)
            .ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new ConsentRequiredException(policy.Field, purpose, decision.Refusal);
        }
    }

    /// <summary>The scope a field act addresses — the record it belongs to, in the same canonical shape the
    /// authorization gate's record scopes use (<c>/records/{id}</c>), so one consent record can cover a
    /// record and a narrower one cannot be stretched over the tenant.</summary>
    public static ScopeExpression ConsentScope(EntityId entity) =>
        ScopeExpression.Parse($"/records/{Uri.EscapeDataString(entity.LocalPart)}");

    private static (string? Value, bool Redacted, bool Masked) ProjectValue(
        ResolvedFieldPolicy policy, Trigger trigger, string? value)
    {
        if (policy.Has(EffectKind.Redact, trigger))
        {
            return (null, true, false); // omit entirely — redact wins over mask (more restrictive)
        }
        if (policy.Effect(EffectKind.Mask, trigger) is { } mask)
        {
            return (MaskValue(value, mask.Params.MaskRevealLast), false, true);
        }
        return (value, false, false);
    }

    internal static string? MaskValue(string? value, int? revealLast)
    {
        if (value is null) return null;
        var keep = revealLast is { } n && n > 0 ? Math.Min(n, value.Length) : 0;
        var maskedCount = value.Length - keep;
        return new string('*', maskedCount) + value.Substring(maskedCount);
    }

    private static string RecordClassOf(ResolvedFieldPolicy policy)
        => policy.Tags.Count > 0 ? policy.Tags[0].Code : "field";

    private AuditAppend BuildAppend(EntityId entity, ActorId actor, TenantId tenant, Op op, string field, string action)
    {
        var payload = JsonSerializer.SerializeToDocument(new { field, action, spine = "2" });
        return new AuditAppend(entity, VersionId: null, op, actor, tenant, _time.GetUtcNow(), payload,
            Justification: $"spine-2 {action}");
    }
}
