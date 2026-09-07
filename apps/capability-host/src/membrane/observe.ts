/**
 * The Observe face (ADR 0124 Part II + Part V — control→data plane) — the
 * telemetry contract. v0 scope (Phase-2): the tri-state HEALTH probe
 * (up/degraded/down) + a redacting structured-log sink. Full metrics/traces/
 * usage aggregation is DEFERRED to v2 (ADR 0124 Part VI).
 *
 * The probe RESULT shape (`HealthProbe`, `HealthState`) is owned by
 * @harborline-software/api-contracts (the single source, X-1). This module re-exports them and
 * adds the membrane-side probe driver + the SEC-3-redacting log sink.
 *
 * --- Supervise (ADR 0124 Part II — control plane) ---
 *
 * NET-1 (ADR 0124 council fold 2026-06-16): the Supervise face — start / health /
 * two-phase shutdown / restart-on-crash — is specified by the ADR-0115 C7
 * supervision spec, cited BY PATH (not by number, so Supervise does not become a
 * third drifting surface):
 *
 *     _shared/engineering/local-node-process-supervision-spec.md
 *
 * Supervise is conditional: REQUIRED for `local-subprocess` addressing, N/A for
 * `in-process` and `remote` (ADR 0124 Part II). It CONSUMES Observe (the liveness
 * probe drives restart-on-crash). v0 does not implement process supervision (no
 * real subprocess spawn in the reference round-trip); the seam below names the
 * spec so a later phase implements it against the cited spec, not a re-derivation.
 */

import type { HealthProbe, HealthState } from '@harborline-software/api-contracts'

import { redactDeep, type RedactionOptions } from './redaction.js'

export type { HealthProbe, HealthState }

/** The canonical path to the ADR-0115 C7 supervision spec (NET-1 — cite by PATH). */
export const SUPERVISION_SPEC_PATH =
  '_shared/engineering/local-node-process-supervision-spec.md' as const

/** The three probe kinds (ADR 0124 Part V — distinct consequences). */
export type ProbeKind = HealthProbe['kind']

/**
 * A runtime-side health-probe source: answers a probe of a given kind with a
 * tri-state result. Implemented by every runtime (the reference loopback runtime
 * implements it; a real runtime exposes systemd-style READY/WATCHDOG + HTTP).
 */
export interface HealthProbeSource {
  probe(kind: ProbeKind): HealthProbe | Promise<HealthProbe>
}

/**
 * Supervise applicability (ADR 0124 Part II): supervision is REQUIRED only for
 * `local-subprocess`. This predicate is the membrane's record of the conditional
 * — the actual restart-on-crash behaviour follows {@link SUPERVISION_SPEC_PATH}.
 */
export function requiresSupervision(mode: string): boolean {
  return mode === 'local-subprocess'
}

/** A structured membrane log record (ADR 0124 Part V — structured + redacted). */
export interface LogRecord {
  level: 'debug' | 'info' | 'warn' | 'error'
  /** The correlation id this record belongs to (preserved through redaction). */
  correlationId?: string
  /** Stable runtime/capability resource attributes (OTel aggregation, Part V). */
  runtimeId?: string
  capabilityId?: string
  /** The human-readable message (REDACTED at emit). */
  message: string
  /** Optional structured fields (REDACTED at emit). */
  fields?: Record<string, unknown>
}

/** A sink the membrane writes redacted log records to (local-first, Part V). */
export interface LogSink {
  write(record: LogRecord): void
}

/**
 * A log sink that scrubs every record through the SEC-3 redactor BEFORE handing
 * it to the underlying sink — the M3-boundary redaction made operational for
 * logs. `correlationId` survives (trace id, not a secret). This is the sink the
 * membrane uses to emit anything derived from a native runtime/provider payload.
 */
export class RedactingLogSink implements LogSink {
  constructor(
    private readonly inner: LogSink,
    private readonly options: RedactionOptions = {},
  ) {}

  write(record: LogRecord): void {
    this.inner.write(redactDeep(record, this.options))
  }
}

/** An in-memory log sink (for tests + local-first default; no off-host export). */
export class InMemoryLogSink implements LogSink {
  readonly records: LogRecord[] = []
  write(record: LogRecord): void {
    this.records.push(record)
  }
}

/**
 * Drive a health probe against a runtime's probe source (the Observe HEALTH path).
 * Normalizes any thrown probe into a `down` result (a runtime that cannot answer
 * a probe is, observably, down — tri-state never throws to the caller).
 */
export async function driveHealthProbe(
  source: HealthProbeSource,
  kind: ProbeKind,
): Promise<HealthProbe> {
  try {
    return await source.probe(kind)
  } catch (err) {
    const detail = err instanceof Error ? err.message : String(err)
    const state: HealthState = 'down'
    return { kind, state, detail }
  }
}
