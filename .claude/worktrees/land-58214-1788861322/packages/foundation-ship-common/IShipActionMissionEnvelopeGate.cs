using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.MissionSpace;

namespace Harborline.Api.Foundation.Ship.Common;

/// <summary>
/// Composes <see cref="IFeatureGate{TFeature}"/> with the
/// <see cref="ShipAction"/> taxonomy per ADR 0077 §2.1 step 2. Hosts wire
/// per-action <see cref="MissionEnvelope"/> evaluations through this
/// adapter; <see cref="DefaultPermissionResolver"/> consumes the
/// <see cref="MissionEnvelopeVerdict"/> directly so it does not need to
/// know which <c>IFeature</c> generic argument to instantiate.
/// </summary>
/// <remarks>
/// Phase 1 ships the contract. Resolvers without a configured gate skip step 2; hosts opt in by
/// registering a concrete <see cref="IShipActionMissionEnvelopeGate"/> against the DI container.
/// </remarks>
public interface IShipActionMissionEnvelopeGate
{
    /// <summary>Evaluate the Mission-Envelope verdict for the supplied action.</summary>
    ValueTask<MissionEnvelopeVerdict> EvaluateAsync(ShipAction action, CancellationToken ct = default);
}

/// <summary>
/// Decision payload returned by <see cref="IShipActionMissionEnvelopeGate.EvaluateAsync"/>.
/// </summary>
/// <param name="IsAvailable">True when the action is available in the current envelope.</param>
/// <param name="ReasonDisplay">Localized human-readable cause when unavailable; ignored when available.</param>
/// <param name="RemediationDisplay">Localized suggested-next-action when unavailable; ignored when available.</param>
/// <param name="CallToActionLabel">Localized affordance label (e.g., <c>"Upgrade edition"</c>) when unavailable; null otherwise.</param>
public sealed record MissionEnvelopeVerdict(
    bool IsAvailable,
    string ReasonDisplay,
    string RemediationDisplay,
    string? CallToActionLabel)
{
    /// <summary>Singleton "available" verdict.</summary>
    public static readonly MissionEnvelopeVerdict Available = new(
        IsAvailable: true,
        ReasonDisplay: string.Empty,
        RemediationDisplay: string.Empty,
        CallToActionLabel: null);
}
