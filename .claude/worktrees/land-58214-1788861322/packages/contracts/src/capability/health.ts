/**
 * The Observe(health) REPORT shape — the cross-runtime roll-up (ADR 0124 Part
 * IV.6 / Part V), distinct from a single `HealthProbe` (`negotiate.ts`).
 *
 * A `HealthProbe` is ONE runtime's tri-state answer to ONE probe. A `HealthReport`
 * is the membrane's roll-up of the readiness probe across EVERY connected runtime
 * — the shape every Observe FACE returns (the renderer's `capability_health` command,
 * the SDK `health()` call, the `harborline-node health` CLI command). It was previously
 * hand-mirrored in the renderer's `src/membrane/client.ts`; lifting it here makes
 * the contracts package the single type source for the renderer + SDK + CLI (the
 * agent-client doctrine's "one typed source, every face a projection").
 *
 * TYPES ONLY — no runtime behaviour; browser-safe (erases at compile).
 * Framework-neutral.
 */

import type { CapabilityId } from './common.js'
import type { HealthState } from './negotiate.js'

/**
 * One runtime's line in the Observe report: which runtime, which capability it
 * hosts, its tri-state health, and an optional human-readable detail. The
 * `state` reuses the canonical tri-state `HealthState` (`up`|`degraded`|`down`)
 * from the Negotiate/Observe surface — a report line is a probe result keyed to
 * its runtime, not a new health vocabulary.
 */
export interface RuntimeHealth {
  /** The connected runtime's id (e.g. `capability-tts-floor-runtime`). */
  runtimeId: string
  /** The capability this runtime hosts (e.g. `tts`, `image`). */
  capability: CapabilityId
  /** Tri-state health from the readiness probe. */
  state: HealthState
  /** Human-readable detail (display string); `null` when the probe gave none. */
  detail: string | null
}

/**
 * The Observe(health) report across every connected runtime — the uniform shape
 * the renderer/SDK/CLI Observe faces all return. A runtime that cannot answer a
 * probe reports `down` (the membrane's `driveHealthProbe` contract never throws),
 * so the report is always complete.
 */
export interface HealthReport {
  /** One line per connected runtime. */
  runtimes: RuntimeHealth[]
}
