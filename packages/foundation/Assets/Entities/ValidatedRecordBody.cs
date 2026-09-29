using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>
/// The gate's allow decision, reduced to the one fact stage two of the write pipeline needs
/// (ADR 0065 clause 4: gate, then validation, then persistence).
/// </summary>
/// <remarks>
/// This seam exists because <c>Harborline.Api.Foundation.Authorization</c> references this assembly,
/// not the other way round: the mint's signature cannot name <c>AuthorizationDecision</c> without a
/// package cycle. <c>AuthorizationDecision</c> is the only production implementation.
/// </remarks>
public interface IWriteAdmission
{
    /// <summary>Whether the gate allowed the act this write carries out.</summary>
    bool IsAllowed { get; }

    /// <summary>
    /// The act's admitted instant, the one the gate decided at. The Rules stage evaluates a record's bound
    /// rules at this instant (T-978), so a rule that reads the date sees the act, not the wall clock.
    /// </summary>
    DateTimeOffset At { get; }
}

/// <summary>
/// A RECORD body that has passed the write pipeline's validation stage, paired with the schema it was
/// validated against. <see cref="AdmitAsync"/> is the only way to obtain one — the constructor is
/// private, so not even an <c>InternalsVisibleTo</c> friend can forge a token — and it validates before
/// it constructs. A writer that reaches the record seam without gate-then-validation is therefore a
/// compile error rather than a convention (ticket 366 slice 1, RW-9; ticket 151 ledger L1418).
/// </summary>
/// <remarks>
/// The token covers ONLY the record seam (<see cref="IEntityMutationStore.CreateAsync(ValidatedRecordBody,
/// CreateOptions, CancellationToken)"/> and its update pair). Definition envelopes and form instances
/// reach the store's internal raw seam and keep their own admission: their bodies are not records, so a
/// record schema cannot judge them and this token must never be minted for one.
/// </remarks>
public sealed class ValidatedRecordBody
{
    private ValidatedRecordBody(SchemaId schema, JsonDocument body)
    {
        Schema = schema;
        Body = body;
    }

    /// <summary>The schema the body was validated against; the store refuses a mismatched record.</summary>
    public SchemaId Schema { get; }

    /// <summary>The validated body.</summary>
    public JsonDocument Body { get; }

    /// <summary>
    /// Validates <paramref name="body"/> against <paramref name="schema"/> and mints its token. Requires
    /// the gate's allowed decision for this write: an unallowed (or absent) admission raises before the
    /// validator runs, so the ADR 0065 clause 4 ordering cannot be swapped.
    /// </summary>
    /// <remarks>
    /// T-978: <paramref name="tenant"/> and <paramref name="binding"/> (the record's bound property form, null
    /// when it has none) reach the validator's Rules stage, which runs that form's record rules.
    /// </remarks>
    /// <exception cref="EntityValidationException">The body does not satisfy the schema or a bound record rule, or the schema is unknown.</exception>
    // holds RW-9 · closes RW-H2: the only construction of a record-write token in the tree, and it cannot
    // be reached without an allowed decision and a completed validation. The mint sites are pinned by
    // ValidatedRecordBodyAdmissionArchTests.
    public static async Task<ValidatedRecordBody> AdmitAsync(
        IEntityValidator validator,
        IWriteAdmission allowed,
        SchemaId schema,
        JsonDocument body,
        TenantId tenant,
        EntityBinding? binding,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(body);
        if (!allowed.IsAllowed)
        {
            throw new InvalidOperationException(
                "entity.validation.not_admitted: the write pipeline's validation stage was reached "
                + "without an allowed gate decision (ADR 0065 clause 4 orders gate, validation, persistence).");
        }

        // T-978: the record overload also carries what the Rules stage reads: the record's tenant, its bound
        // property form, and the admitted act instant.
        await validator.ValidateRecordAsync(schema, body, tenant, binding, allowed.At, ct).ConfigureAwait(false);
        return new ValidatedRecordBody(schema, body);
    }
}
