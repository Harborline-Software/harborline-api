using System;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.UI;

namespace Harborline.Api.UICore.Wayfinder.Widgets;

/// <summary>
/// Sync-state Helm widget per ADR 0066 §1.4 (W#53 Phase 2 PR 2a).
/// Renders the ambient
/// <see cref="Harborline.Api.Foundation.MissionSpace.MissionEnvelope.SyncState"/>
/// with a localized label drawn from
/// <c>Harborline.Api.Foundation.UI.SyncStateExtensions.ToCanonicalIdentifier</c>.
/// </summary>
/// <remarks>
/// <b>Slot:</b> <see cref="HelmSlot.GlanceBand"/>; OrderHint 200.
/// Unwraps <c>SyncStateSnapshot.State</c> from
/// <c>MissionEnvelope.SyncState</c> before rendering — the snapshot
/// record carries the enum + its observability metadata; the widget
/// surfaces only the enum to the GlanceBand label.
/// </remarks>
public sealed class SyncStateWidget : IHelmWidget
{
    /// <inheritdoc />
    public HelmWidgetMetadata Metadata { get; } = new(
        WidgetId: "sync-state",
        Slot: HelmSlot.GlanceBand,
        OrderHint: 200,
        AccessibleName: "Sync state",
        CapabilityGateType: null);

    /// <inheritdoc />
    public ValueTask<HelmWidgetViewState> ComputeAsync(
        HelmRenderContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = context.Envelope.SyncState;
        var state = snapshot.State;
        var label = state.ToCanonicalIdentifier();

        var view = new HelmWidgetViewState(
            State: state,
            PrimaryLabel: label,
            SecondaryLabel: snapshot.LastSyncedAt is { } lastSynced
                ? $"Last peer exchange {lastSynced:u}; currentness not established"
                : null,
            Actions: Array.Empty<HelmWidgetAction>());
        return ValueTask.FromResult(view);
    }
}
