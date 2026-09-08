/**
 * DeviceCapabilityProfile — the host-owned, fail-safe input to local-model
 * admission (ADR 0116 hardware stage consumed by ADR 0132).
 *
 * A tier is a conservative CAPACITY CEILING, never proof that a model is
 * installed, intact, licensed, healthy, or performant. Those remain separate
 * ADR 0132 eligibility gates.
 */

export const DeviceCapabilityTier = {
  Embedding: 'T-E',
  SmallChat: 'T-S',
  Assistant: 'T-A',
  Review: 'T-R',
  Concurrent: 'T-C',
} as const

export type DeviceCapabilityTier =
  (typeof DeviceCapabilityTier)[keyof typeof DeviceCapabilityTier]

export type DeviceOsFamily = 'macos' | 'windows' | 'linux' | 'ios' | 'android' | 'unknown'

export type FastMemoryKind = 'unified' | 'dedicatedVram' | 'system' | 'unknown'

export type MemoryBandwidthClass = 'low' | 'moderate' | 'high' | 'unknown'

export type BandwidthEvidence = 'inferred' | 'unknown'

export type DeviceCapabilityDetectionStatus = 'complete' | 'partial' | 'unknown'

export type DeviceCapabilityLimitation =
  | 'bandwidthUnavailable'
  | 'dedicatedVramUnavailable'
  | 'fastMemoryUnavailable'
  | 'hostUnavailable'
  | 'invalidHostResponse'
  | 'mobileWorkingSetEstimated'
  | 'probeFailed'

export interface DeviceCapabilityProfile {
  schemaVersion: 1
  detectedAtMs: number
  osFamily: DeviceOsFamily
  architecture: string
  systemMemoryBytes: number | null
  fastMemoryBytes: number | null
  fastMemoryKind: FastMemoryKind
  bandwidthClass: MemoryBandwidthClass
  bandwidthEvidence: BandwidthEvidence
  maximumLocalAiTier: DeviceCapabilityTier
  detectionStatus: DeviceCapabilityDetectionStatus
  limitations: DeviceCapabilityLimitation[]
}

const TIER_RANK: Record<DeviceCapabilityTier, number> = {
  'T-E': 0,
  'T-S': 1,
  'T-A': 2,
  'T-R': 3,
  'T-C': 4,
}

const OS_FAMILIES = new Set<DeviceOsFamily>([
  'macos',
  'windows',
  'linux',
  'ios',
  'android',
  'unknown',
])
const FAST_MEMORY_KINDS = new Set<FastMemoryKind>([
  'unified',
  'dedicatedVram',
  'system',
  'unknown',
])
const BANDWIDTH_CLASSES = new Set<MemoryBandwidthClass>([
  'low',
  'moderate',
  'high',
  'unknown',
])
const BANDWIDTH_EVIDENCE = new Set<BandwidthEvidence>(['inferred', 'unknown'])
const DETECTION_STATUSES = new Set<DeviceCapabilityDetectionStatus>([
  'complete',
  'partial',
  'unknown',
])
const LIMITATIONS = new Set<DeviceCapabilityLimitation>([
  'bandwidthUnavailable',
  'dedicatedVramUnavailable',
  'fastMemoryUnavailable',
  'hostUnavailable',
  'invalidHostResponse',
  'mobileWorkingSetEstimated',
  'probeFailed',
])

/** True when a confirmed device ceiling admits a local model's declared floor. */
export function admitsLocalAiTier(
  profile: DeviceCapabilityProfile,
  requiredTier: DeviceCapabilityTier,
): boolean {
  return TIER_RANK[profile.maximumLocalAiTier] >= TIER_RANK[requiredTier]
}

/**
 * Validate the Tauri wire payload before it reaches the UI or resolver. A bad
 * payload is treated exactly like a failed probe by the renderer client.
 */
export function parseDeviceCapabilityProfile(value: unknown): DeviceCapabilityProfile {
  if (!isRecord(value)) throw new TypeError('DeviceCapabilityProfile: expected object')
  if (value['schemaVersion'] !== 1) {
    throw new TypeError('DeviceCapabilityProfile: unsupported schemaVersion')
  }
  if (!isNonNegativeFiniteNumber(value['detectedAtMs'])) {
    throw new TypeError('DeviceCapabilityProfile: invalid detectedAtMs')
  }
  if (!OS_FAMILIES.has(value['osFamily'] as DeviceOsFamily)) {
    throw new TypeError('DeviceCapabilityProfile: invalid osFamily')
  }
  if (typeof value['architecture'] !== 'string' || value['architecture'].length === 0) {
    throw new TypeError('DeviceCapabilityProfile: invalid architecture')
  }
  if (!isNullableByteCount(value['systemMemoryBytes'])) {
    throw new TypeError('DeviceCapabilityProfile: invalid systemMemoryBytes')
  }
  if (!isNullableByteCount(value['fastMemoryBytes'])) {
    throw new TypeError('DeviceCapabilityProfile: invalid fastMemoryBytes')
  }
  if (!FAST_MEMORY_KINDS.has(value['fastMemoryKind'] as FastMemoryKind)) {
    throw new TypeError('DeviceCapabilityProfile: invalid fastMemoryKind')
  }
  if (!BANDWIDTH_CLASSES.has(value['bandwidthClass'] as MemoryBandwidthClass)) {
    throw new TypeError('DeviceCapabilityProfile: invalid bandwidthClass')
  }
  if (!BANDWIDTH_EVIDENCE.has(value['bandwidthEvidence'] as BandwidthEvidence)) {
    throw new TypeError('DeviceCapabilityProfile: invalid bandwidthEvidence')
  }
  if (
    typeof value['maximumLocalAiTier'] !== 'string' ||
    !(value['maximumLocalAiTier'] in TIER_RANK)
  ) {
    throw new TypeError('DeviceCapabilityProfile: invalid maximumLocalAiTier')
  }
  const safeCeiling = capacityCeiling(
    value['fastMemoryBytes'] as number | null,
    value['fastMemoryKind'] as FastMemoryKind,
  )
  if (
    TIER_RANK[value['maximumLocalAiTier'] as DeviceCapabilityTier] > TIER_RANK[safeCeiling]
  ) {
    throw new TypeError('DeviceCapabilityProfile: maximumLocalAiTier exceeds hardware evidence')
  }
  if (!DETECTION_STATUSES.has(value['detectionStatus'] as DeviceCapabilityDetectionStatus)) {
    throw new TypeError('DeviceCapabilityProfile: invalid detectionStatus')
  }
  if (
    !Array.isArray(value['limitations']) ||
    !value['limitations'].every((item) => LIMITATIONS.has(item as DeviceCapabilityLimitation))
  ) {
    throw new TypeError('DeviceCapabilityProfile: invalid limitations')
  }
  return value as unknown as DeviceCapabilityProfile
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function isNonNegativeFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0
}

function isNullableByteCount(value: unknown): value is number | null {
  return value === null || (isNonNegativeFiniteNumber(value) && Number.isSafeInteger(value))
}

function capacityCeiling(bytes: number | null, kind: FastMemoryKind): DeviceCapabilityTier {
  if (bytes === null || kind === 'unknown' || bytes < 3 * 1024 ** 3) return 'T-E'
  if (kind === 'system') return 'T-S'
  if (bytes < 8 * 1024 ** 3) return 'T-S'
  if (bytes < 18 * 1024 ** 3) return 'T-A'
  if (bytes < 48 * 1024 ** 3) return 'T-R'
  return 'T-C'
}
