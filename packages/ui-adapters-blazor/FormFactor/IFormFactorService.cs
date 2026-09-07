using System;
using System.Threading.Tasks;

namespace Harborline.Api.UIAdapters.Blazor.FormFactor;

/// <summary>
/// Idiomatic Blazor mirror of the ui-react form-factor hook family (FF3) — reads the SAME
/// named queries (<see cref="FormFactorModeQueries"/> / <see cref="FormFactorSubSignalQueries"/> /
/// <see cref="FormFactorCapabilityQueries"/>, generated from
/// <c>_shared/design/form-factor.contract.json</c>) via matchMedia JS interop and resolves them
/// with the SAME pure resolver (<see cref="FormFactorResolver.Resolve"/>) the TS runtime uses.
/// See <c>_shared/design/form-factor-contract-2026-07-07.md</c> §6 FF10.
/// </summary>
public interface IFormFactorService
{
    /// <summary>
    /// The last-resolved form factor state. Before <see cref="InitializeAsync"/> completes this is
    /// the resolver's output over an all-false <see cref="MatchedMediaState"/> (mode resolves to
    /// <see cref="FormFactorMode.Tablet"/>, the axis's residual/neutral value) — a deliberately
    /// inert placeholder for the pre-interop render pass, mirroring the TS runtime's SSR-safe
    /// default (§3.2: "returns false server-side, measures on mount").
    /// </summary>
    ResolvedFormFactor Current { get; }

    /// <summary>True once the first real matchMedia read has completed.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Raised whenever a tracked media query's match state changes (viewport resize, orientation
    /// change, pointer/hover capability change) — including the transition out of the placeholder
    /// state once <see cref="InitializeAsync"/> completes.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// Performs the first matchMedia read and wires change listeners via JS interop. Idempotent —
    /// safe to call from every consumer's <c>OnAfterRenderAsync(firstRender)</c>; only the first
    /// call does real work.
    /// </summary>
    Task InitializeAsync();
}
