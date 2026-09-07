namespace Harborline.Api.Foundation.Coordination;

/// <summary>Names one invariant that a coordinated write may require.</summary>
public sealed record WriteInvariant
{
    /// <summary>Creates an invariant identifier from its stable name.</summary>
    public WriteInvariant(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The stable invariant name.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The staged substrate-owned unit that write-enlistment adapters act upon. Concrete hosts carry their
/// context and domain payload in a derived type; neither appears on the Platform enlistment signature.
/// </summary>
public abstract class StagedWriteUnitOfWork;

/// <summary>The result of running a registered write-enlistment adapter.</summary>
public enum WriteEnlistmentOutcome
{
    /// <summary>The adapter is registered, but its invariant does not apply to this write.</summary>
    NotApplicable,

    /// <summary>The adapter enlisted its invariant in the staged unit of work.</summary>
    Enlisted,
}

/// <summary>
/// Adapts one coordinated-write invariant to a staged unit of work without exposing domain content in
/// the Platform contract.
/// </summary>
public interface IWriteEnlistment
{
    /// <summary>The invariant handled by this adapter.</summary>
    WriteInvariant Invariant { get; }

    /// <summary>Enlists the invariant or reports that it is not applicable to this write.</summary>
    ValueTask<WriteEnlistmentOutcome> EnlistAsync(
        StagedWriteUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default);
}

/// <summary>An operation and the complete set of invariants it requires at its write chokepoint.</summary>
public sealed class DeclaredWriteOperation
{
    /// <summary>Creates a declared operation.</summary>
    public DeclaredWriteOperation(string name, IEnumerable<WriteInvariant> requiredInvariants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requiredInvariants);

        Name = name;
        RequiredInvariants = requiredInvariants.Distinct().ToArray();
    }

    /// <summary>The stable operation name.</summary>
    public string Name { get; }

    /// <summary>The invariants the operation refuses to save without.</summary>
    public IReadOnlyList<WriteInvariant> RequiredInvariants { get; }
}

/// <summary>Raised before enlistment when a declared invariant has no registered adapter.</summary>
public sealed class MissingWriteEnlistmentException : InvalidOperationException
{
    /// <summary>Creates a fail-closed missing-adapter error.</summary>
    public MissingWriteEnlistmentException(string operationName, WriteInvariant invariant)
        : base($"Operation '{operationName}' requires write invariant '{invariant.Name}', but no handler is registered; save refused.")
    {
        OperationName = operationName;
        Invariant = invariant;
    }

    /// <summary>The operation whose save was refused.</summary>
    public string OperationName { get; }

    /// <summary>The declared invariant that had no handler.</summary>
    public WriteInvariant Invariant { get; }
}

/// <summary>
/// Resolves declared invariants to adapters. The one configured fence invariant runs first; remaining
/// invariants retain declaration order and have no priority surface.
/// </summary>
public sealed class WriteEnlistmentRegistry
{
    private readonly IReadOnlyDictionary<WriteInvariant, IWriteEnlistment> _handlers;
    private readonly WriteInvariant _fenceFirstInvariant;

    /// <summary>Creates a registry with its single explicit fence-first constraint.</summary>
    public WriteEnlistmentRegistry(
        IEnumerable<IWriteEnlistment> handlers,
        WriteInvariant fenceFirstInvariant)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(fenceFirstInvariant);

        _handlers = handlers.ToDictionary(handler => handler.Invariant);
        _fenceFirstInvariant = fenceFirstInvariant;
    }

    /// <summary>
    /// Validates that every declared invariant has a handler, then enlists the fence first and the
    /// remaining requirements in declaration order.
    /// </summary>
    public async Task<IReadOnlyDictionary<WriteInvariant, WriteEnlistmentOutcome>> EnlistAsync(
        DeclaredWriteOperation operation,
        StagedWriteUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        foreach (var invariant in operation.RequiredInvariants)
        {
            if (!_handlers.ContainsKey(invariant))
            {
                throw new MissingWriteEnlistmentException(operation.Name, invariant);
            }
        }

        var outcomes = new Dictionary<WriteInvariant, WriteEnlistmentOutcome>();
        if (operation.RequiredInvariants.Contains(_fenceFirstInvariant))
        {
            outcomes[_fenceFirstInvariant] = await _handlers[_fenceFirstInvariant]
                .EnlistAsync(unitOfWork, cancellationToken).ConfigureAwait(false);
        }

        foreach (var invariant in operation.RequiredInvariants)
        {
            if (invariant == _fenceFirstInvariant)
            {
                continue;
            }

            outcomes[invariant] = await _handlers[invariant]
                .EnlistAsync(unitOfWork, cancellationToken).ConfigureAwait(false);
        }

        return outcomes;
    }
}
