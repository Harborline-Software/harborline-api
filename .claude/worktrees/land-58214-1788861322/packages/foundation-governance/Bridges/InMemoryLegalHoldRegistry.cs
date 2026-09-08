using System.Collections.Concurrent;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Governance.Bridges;

/// <summary>
/// In-memory <see cref="ILegalHoldRegistry"/>. Holds are placed / lifted by an operator
/// action (the host wires the authority); the registry only answers "is held?".
/// </summary>
public sealed class InMemoryLegalHoldRegistry : ILegalHoldRegistry
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    /// <summary>Place a legal hold on a subject.</summary>
    public void Place(TenantId tenant, SubjectId subject) => _held[Key(tenant, subject)] = 1;

    /// <summary>Lift a legal hold.</summary>
    public void Lift(TenantId tenant, SubjectId subject) => _held.TryRemove(Key(tenant, subject), out _);

    /// <inheritdoc />
    public bool IsHeld(TenantId tenant, SubjectId subject) => _held.ContainsKey(Key(tenant, subject));

    private static string Key(TenantId tenant, SubjectId subject) => tenant.Value + "|" + subject.Value;
}
