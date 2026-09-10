using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>
/// The gate's allow decision, reduced to the one fact stage two of the write pipeline needs
/// (ADR 0065 clause 4: gate, then validation, then persistence).
/// </summary>
/// <remarks>
/// This seam exists because <c>Harborline.Api.Foundation.Authorization</c> references this
/// assembly, not the other way round: the store's signature cannot name
/// <c>AuthorizationDecision</c> without a package cycle. <c>AuthorizationDecision</c> is the
/// only production implementation.
/// </remarks>
public interface IWriteAdmission
{
    /// <summary>Whether the gate allowed the act this write carries out.</summary>
    bool IsAllowed { get; }
}

/// <summary>
/// A body that has passed the write pipeline's validation stage, paired with the schema it was
/// validated against. Only <see cref="EntityBodyAdmission"/> can construct one, so a writer that
/// has not been through gate-then-validation cannot call
/// <see cref="IEntityMutationStore.CreateAsync"/> or
/// <see cref="IEntityMutationStore.UpdateAsync"/> — the bypass is a compile error, not a
/// convention (ticket 151, ledger L1418).
/// </summary>
public sealed class ValidatedBody
{
    internal ValidatedBody(SchemaId schema, JsonDocument body)
    {
        Schema = schema;
        Body = body;
    }

    /// <summary>The schema the body was validated against; the store refuses a mismatched record.</summary>
    public SchemaId Schema { get; }

    /// <summary>The validated body.</summary>
    public JsonDocument Body { get; }
}

/// <summary>
/// The write pipeline's second stage, and the only mint of <see cref="ValidatedBody"/>.
/// </summary>
public sealed class EntityBodyAdmission
{
    private readonly Func<IEntityValidator> _validator;

    /// <summary>Binds a fixed validator (tests and hand-wired compositions).</summary>
    public EntityBodyAdmission(IEntityValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = () => validator;
    }

    /// <summary>
    /// Binds the validator lazily, so a composition that only mints envelope or engine-validated
    /// tokens (a definition store with no record type in sight) does not have to register an
    /// <see cref="IEntityValidator"/> to build its graph. The record path is unchanged: the first
    /// <see cref="AdmitAsync"/> resolves the real validator, and a composition without one raises
    /// there rather than persisting an unvalidated body.
    /// </summary>
    public EntityBodyAdmission(Func<IEntityValidator> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    /// <summary>
    /// Validates a caller-supplied body against <paramref name="schema"/> and mints its token.
    /// Requires the gate's allowed decision for this write: an unallowed (or absent) admission
    /// raises before the validator runs, so the ordering cannot be swapped.
    /// </summary>
    /// <exception cref="EntityValidationException">The body does not satisfy the schema, or the schema is unknown.</exception>
    public async Task<ValidatedBody> AdmitAsync(
        IWriteAdmission allowed,
        SchemaId schema,
        JsonDocument body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(body);
        if (!allowed.IsAllowed)
        {
            throw new InvalidOperationException(
                "entity.validation.not_admitted: the write pipeline's validation stage was reached "
                + "without an allowed gate decision (ADR 0065 clause 4 orders gate, validation, persistence).");
        }

        await _validator().ValidateAsync(schema, body, ct).ConfigureAwait(false);
        return new ValidatedBody(schema, body);
    }

    /// <summary>
    /// Mints a token for a body its own engine already validated against the same schema registry
    /// and then transformed, so that re-running <see cref="IEntityValidator"/> over the stored form
    /// would be wrong rather than redundant: a form instance whose Sensitive fields are ciphertext,
    /// and a definition envelope serialized by an in-tree writer under a schema the registry does
    /// not hold. The caller must present the raw mutation port it is about to write through — that
    /// port is deliberately unregistered in DI, so possession of it is the admission, and every
    /// holder is enumerated by <c>RawMutationPortSymbolInventoryTests</c>.
    /// </summary>
    /// <param name="port">The unregistered mutation port the caller holds.</param>
    /// <param name="schema">The schema the body is an instance of.</param>
    /// <param name="body">The already-validated body.</param>
    /// <param name="provenance">Which engine validated it; recorded in the refusal when absent.</param>
    public ValidatedBody AdmitOwnValidated(
        IEntityMutationStore port,
        SchemaId schema,
        JsonDocument body,
        string provenance)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenance);
        return new ValidatedBody(schema, body);
    }
}
