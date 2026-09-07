// GENERATED from packages/contracts/protocol. DO NOT EDIT.
// Protocol shipyard.carrier 2.0.0.

export const HARBORLINE_PROTOCOL_ID = 'shipyard.carrier' as const
export const HARBORLINE_PROTOCOL_VERSION = '2.0.0' as const

export const HARBORLINE_OPERATION_IDS = {
  CAPABILITY_ANNOUNCE: 'capability.announce',
  CAPABILITY_NEGOTIATE: 'capability.negotiate',
  CAPABILITY_ADDRESS: 'capability.address',
  CAPABILITY_SECURE: 'capability.secure',
  CAPABILITY_INVOKE: 'capability.invoke',
  CAPABILITY_OBSERVE: 'capability.observe',
  CAPABILITY_RESOLVE: 'capability.resolve',
  CAPABILITY_COMPOSE: 'capability.compose',
  CARRIER_HOST_CAPABILITY_INVOKE: 'carrier.host.capabilityInvoke',
  CARRIER_HOST_CAPABILITY_HEALTH: 'carrier.host.capabilityHealth',
  CARRIER_HOST_CAPABILITY_CP_DEMO_EXECUTE: 'carrier.host.capabilityCpDemoExecute',
  CARRIER_HOST_CURRENT_PRINCIPAL: 'carrier.host.currentPrincipal',
  CARRIER_HOST_NODE_STATUS: 'carrier.host.nodeStatus',
  CARRIER_HOST_DATA_LOCATION_STATUS: 'carrier.host.dataLocationStatus',
  CARRIER_HOST_DEVICE_CAPABILITY_PROFILE: 'carrier.host.deviceCapabilityProfile',
  CARRIER_HOST_GET_PEER_SYNC_CONFIG: 'carrier.host.getPeerSyncConfig',
  CARRIER_HOST_SET_PEER_SYNC_CONFIG: 'carrier.host.setPeerSyncConfig',
  CARRIER_HOST_APPEND_RENDERER_LOG: 'carrier.host.appendRendererLog',
  CARRIER_APPLICATION_GET_SYNC_STATUS: 'carrier.application.getSyncStatus',
} as const

export const HARBORLINE_HOST_COMMANDS = {
  capabilityInvoke: 'capability_invoke',
  capabilityHealth: 'capability_health',
  capabilityCpDemoExecute: 'capability_cp_demo_execute',
  currentPrincipal: 'current_principal',
  nodeStatus: 'node_status',
  dataLocationStatus: 'get_data_location_status',
  deviceCapabilityProfile: 'get_device_capability_profile',
  getPeerSyncConfig: 'get_peer_sync_config',
  setPeerSyncConfig: 'set_peer_sync_config',
  appendRendererLog: 'append_renderer_log',
} as const

export const HARBORLINE_APPLICATION_ROUTES = {
  getSyncStatus: '/api/local-node/sync-status',
} as const

export type EmptyRequest = Record<string, never>

export type EmptyResponse = Record<string, never>

export interface Principal {
  id: string
  displayName: string
  kind: "local-os-user" | "service"
}

export interface ProtocolError {
  faultDomain: "input" | "provider" | "membrane"
  retryable: boolean
  code: string
  message: string
  retryAfter?: number
}

export interface Usage {
  unit: string
  quantity: number
  costMicros?: number | null
  tier: "local" | "remote" | "cloud"
}

export interface Artifact {
  kind: string
  uri?: string
  mime?: string
  text?: string
  [key: string]: unknown
}

export interface CapabilityResult {
  jobId: string
  status: "accepted" | "running" | "succeeded" | "partial" | "failed"
  progress: number
  artifacts: Artifact[]
  usage: Usage
  error: ProtocolError | null
}

export interface RuntimeHealth {
  runtimeId: string
  capability: string
  state: "up" | "degraded" | "down"
  detail: string | null
  extensions?: Record<string, unknown>
}

export interface HealthReport {
  runtimes: RuntimeHealth[]
}

export interface ProviderDescriptor {
  id: string
  [key: string]: unknown
}

export interface RuntimeCapability {
  capabilityId: string
  schemaVersion: string
  providers: ProviderDescriptor[]
  extensions?: Record<string, unknown>
}

export interface AnnounceRequest {
  runtimeId: string
}

export interface AnnounceResult {
  runtimeId: string
  name: string
  contractVersion: string
  capabilities: RuntimeCapability[]
  extensions?: Record<string, unknown>
}

export interface NegotiateRequest {
  runtimeId: string
  contractVersion: string
  capabilitySchemaVersions: Record<string, string>
}

export interface NegotiateResult {
  compatible: boolean
  agreedContractVersion: string
  acceptedCapabilities: string[]
  reason: string | null
}

export interface AddressRequest {
  runtimeId: string
}

export interface AddressResult {
  runtimeId: string
  mode: "in-process" | "local-subprocess" | "remote-mesh" | "relay"
  baseUrl?: string
  extensions?: Record<string, unknown>
}

export interface SecureRequest {
  operationId: string
  capabilityId: string
  correlationId: string
}

export interface SecureResult {
  allowed: boolean
  authority: "AP" | "CP"
  reason: string | null
}

export interface CapabilityInvokeRequest {
  capability: string
  core: Record<string, unknown>
  correlationId: string
  idempotencyKey: string
}

export interface CapabilityHostInvokeRequest {
  capability: string
  core: Record<string, unknown>
}

export interface ObserveRequest {
  runtimeId?: string
  kind: "startup" | "liveness" | "readiness"
}

export interface ResolveRequest {
  capabilityId: string
}

export interface ResolutionResult {
  chosenProviderId: string | null
  tier: "local" | "remote" | "cloud"
  resolutionState: "available" | "locked-entitlement" | "upsell" | "unavailable-hardware" | "degraded" | "not-in-edition"
  selectionReason: "only-candidate" | "preferred-by-policy" | "hardware-fit" | "fallback-floor" | "entitlement-gated"
  speedHint: "fast" | "moderate" | "slow"
  reason: string
  isFallback: boolean
  extensions?: Record<string, unknown>
}

export interface ComposeRequest {
  editionId: string
  capabilityIds: string[]
}

export interface ComposeResult {
  editionId: string
  acceptedCapabilityIds: string[]
  rejectedCapabilityIds: string[]
}

export interface CpDemoRequest {
  note: string | null
}

export interface CpDemoMeta {
  command: "demo-cp-op"
  confirmed: boolean
  note: string
  executedAt: string
  proposedBy: Principal
  confirmedBy: Principal
}

export interface CpDemoResult {
  jobId: string
  status: "accepted" | "running" | "succeeded" | "partial" | "failed"
  progress: number
  artifacts: Artifact[]
  usage: Usage
  error: ProtocolError | null
  meta?: CpDemoMeta
}

export interface NodeStatus {
  state: "starting" | "running" | "failed" | "stopped"
  baseUrl: string | null
  sessionToken: string | null
  detail?: string
  extensions?: Record<string, unknown>
}

export interface BackupStatus {
  state: "notConfigured" | "configured"
  destination: string | null
  lastSuccessfulBackupAtMs: number | null
}

export interface DataLocationStatus {
  dataDirectory: string
  dataDirectorySource: "applicationData" | "developmentOverride"
  backup: BackupStatus
  extensions?: Record<string, unknown>
}

export interface DeviceCapabilityProfile {
  schemaVersion: 1
  detectedAtMs: number
  osFamily: "macos" | "windows" | "linux" | "ios" | "android" | "unknown"
  architecture: string
  systemMemoryBytes: number | null
  fastMemoryBytes: number | null
  fastMemoryKind: "unified" | "dedicatedVram" | "system" | "unknown"
  bandwidthClass: "low" | "moderate" | "high" | "unknown"
  bandwidthEvidence: "inferred" | "unknown"
  maximumLocalAiTier: "T-E" | "T-S" | "T-A" | "T-R" | "T-C"
  detectionStatus: "complete" | "partial" | "unknown"
  limitations: ("bandwidthUnavailable" | "dedicatedVramUnavailable" | "fastMemoryUnavailable" | "hostUnavailable" | "invalidHostResponse" | "mobileWorkingSetEstimated" | "probeFailed")[]
  extensions?: Record<string, unknown>
}

export interface PeerSyncConfig {
  enableMdns: boolean | null
  peers: string[]
  bindAddress: string | null
}

export interface RendererLogEntry {
  level: string
  message: string
  stack?: string
}

export interface SyncPeerStatus {
  deviceId: string
  label: string
  state: "has" | "will" | "should" | "couldnt"
  lastReachedAt: string | null
  offlineDurationMs: number | null
  isSecurityEvent: boolean
  errorCode: string | null
}

export interface SyncCadence {
  nextRoundAt: string | null
  roundIntervalSeconds: number
}

export interface EnrollmentStatus {
  complete: boolean
  joinedTeamId: string | null
  reason: string | null
}

export interface LastPeerExchange {
  peerDeviceId: string
  peerLabel: string
  exchangedAt: string
}

export interface SyncRecency {
  basis: string
  currentness: string
  lastExchange: LastPeerExchange | null
}

export interface HarborlineSyncStatus {
  aggregate: "has" | "will" | "should" | "couldnt"
  asOf: string
  peers: SyncPeerStatus[]
  cadence: SyncCadence
  recency: SyncRecency
  enrollment?: EnrollmentStatus
  extensions?: Record<string, unknown>
}

export interface HarborlineProtocolModels {
  EmptyRequest: EmptyRequest
  EmptyResponse: EmptyResponse
  Principal: Principal
  ProtocolError: ProtocolError
  Usage: Usage
  Artifact: Artifact
  CapabilityResult: CapabilityResult
  RuntimeHealth: RuntimeHealth
  HealthReport: HealthReport
  ProviderDescriptor: ProviderDescriptor
  RuntimeCapability: RuntimeCapability
  AnnounceRequest: AnnounceRequest
  AnnounceResult: AnnounceResult
  NegotiateRequest: NegotiateRequest
  NegotiateResult: NegotiateResult
  AddressRequest: AddressRequest
  AddressResult: AddressResult
  SecureRequest: SecureRequest
  SecureResult: SecureResult
  CapabilityInvokeRequest: CapabilityInvokeRequest
  CapabilityHostInvokeRequest: CapabilityHostInvokeRequest
  ObserveRequest: ObserveRequest
  ResolveRequest: ResolveRequest
  ResolutionResult: ResolutionResult
  ComposeRequest: ComposeRequest
  ComposeResult: ComposeResult
  CpDemoRequest: CpDemoRequest
  CpDemoMeta: CpDemoMeta
  CpDemoResult: CpDemoResult
  NodeStatus: NodeStatus
  BackupStatus: BackupStatus
  DataLocationStatus: DataLocationStatus
  DeviceCapabilityProfile: DeviceCapabilityProfile
  PeerSyncConfig: PeerSyncConfig
  RendererLogEntry: RendererLogEntry
  SyncPeerStatus: SyncPeerStatus
  SyncCadence: SyncCadence
  EnrollmentStatus: EnrollmentStatus
  LastPeerExchange: LastPeerExchange
  SyncRecency: SyncRecency
  HarborlineSyncStatus: HarborlineSyncStatus
}

export function parseHarborlineProtocolModel<K extends keyof HarborlineProtocolModels>(
  model: K,
  value: unknown,
): HarborlineProtocolModels[K] {
  switch (model) {
    case 'EmptyRequest': assertEmptyRequest(value, model); break
    case 'EmptyResponse': assertEmptyResponse(value, model); break
    case 'Principal': assertPrincipal(value, model); break
    case 'ProtocolError': assertProtocolError(value, model); break
    case 'Usage': assertUsage(value, model); break
    case 'Artifact': assertArtifact(value, model); break
    case 'CapabilityResult': assertCapabilityResult(value, model); break
    case 'RuntimeHealth': assertRuntimeHealth(value, model); break
    case 'HealthReport': assertHealthReport(value, model); break
    case 'ProviderDescriptor': assertProviderDescriptor(value, model); break
    case 'RuntimeCapability': assertRuntimeCapability(value, model); break
    case 'AnnounceRequest': assertAnnounceRequest(value, model); break
    case 'AnnounceResult': assertAnnounceResult(value, model); break
    case 'NegotiateRequest': assertNegotiateRequest(value, model); break
    case 'NegotiateResult': assertNegotiateResult(value, model); break
    case 'AddressRequest': assertAddressRequest(value, model); break
    case 'AddressResult': assertAddressResult(value, model); break
    case 'SecureRequest': assertSecureRequest(value, model); break
    case 'SecureResult': assertSecureResult(value, model); break
    case 'CapabilityInvokeRequest': assertCapabilityInvokeRequest(value, model); break
    case 'CapabilityHostInvokeRequest': assertCapabilityHostInvokeRequest(value, model); break
    case 'ObserveRequest': assertObserveRequest(value, model); break
    case 'ResolveRequest': assertResolveRequest(value, model); break
    case 'ResolutionResult': assertResolutionResult(value, model); break
    case 'ComposeRequest': assertComposeRequest(value, model); break
    case 'ComposeResult': assertComposeResult(value, model); break
    case 'CpDemoRequest': assertCpDemoRequest(value, model); break
    case 'CpDemoMeta': assertCpDemoMeta(value, model); break
    case 'CpDemoResult': assertCpDemoResult(value, model); break
    case 'NodeStatus': assertNodeStatus(value, model); break
    case 'BackupStatus': assertBackupStatus(value, model); break
    case 'DataLocationStatus': assertDataLocationStatus(value, model); break
    case 'DeviceCapabilityProfile': assertDeviceCapabilityProfile(value, model); break
    case 'PeerSyncConfig': assertPeerSyncConfig(value, model); break
    case 'RendererLogEntry': assertRendererLogEntry(value, model); break
    case 'SyncPeerStatus': assertSyncPeerStatus(value, model); break
    case 'SyncCadence': assertSyncCadence(value, model); break
    case 'EnrollmentStatus': assertEnrollmentStatus(value, model); break
    case 'LastPeerExchange': assertLastPeerExchange(value, model); break
    case 'SyncRecency': assertSyncRecency(value, model); break
    case 'HarborlineSyncStatus': assertHarborlineSyncStatus(value, model); break
    default: throw new Error(`unknown Harborline protocol model: ${String(model)}`)
  }
  return value as HarborlineProtocolModels[K]
}

function protocolObject(value: unknown, path: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${path}: expected object`)
  }
  return value as Record<string, unknown>
}

function hasOwn(object: object, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(object, key)
}

function assertEmptyRequest(value: unknown, path: string): asserts value is EmptyRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>([])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
}

function assertEmptyResponse(value: unknown, path: string): asserts value is EmptyResponse {
  const object = protocolObject(value, path)
  const allowed = new Set<string>([])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
}

function assertPrincipal(value: unknown, path: string): asserts value is Principal {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["id","displayName","kind"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "id")) throw new Error(`${path}: missing required property id`)
  if (typeof object["id"] !== 'string') throw new Error(`${path}.id` + ': expected string')
  if (!hasOwn(object, "displayName")) throw new Error(`${path}: missing required property displayName`)
  if (typeof object["displayName"] !== 'string') throw new Error(`${path}.displayName` + ': expected string')
  if (!hasOwn(object, "kind")) throw new Error(`${path}: missing required property kind`)
  if (!["local-os-user","service"].includes(object["kind"] as never)) throw new Error(`${path}.kind` + ': value is outside enum')
}

function assertProtocolError(value: unknown, path: string): asserts value is ProtocolError {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["faultDomain","retryable","code","message","retryAfter"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "faultDomain")) throw new Error(`${path}: missing required property faultDomain`)
  if (!["input","provider","membrane"].includes(object["faultDomain"] as never)) throw new Error(`${path}.faultDomain` + ': value is outside enum')
  if (!hasOwn(object, "retryable")) throw new Error(`${path}: missing required property retryable`)
  if (typeof object["retryable"] !== 'boolean') throw new Error(`${path}.retryable` + ': expected boolean')
  if (!hasOwn(object, "code")) throw new Error(`${path}: missing required property code`)
  if (typeof object["code"] !== 'string') throw new Error(`${path}.code` + ': expected string')
  if (!hasOwn(object, "message")) throw new Error(`${path}: missing required property message`)
  if (typeof object["message"] !== 'string') throw new Error(`${path}.message` + ': expected string')
  if (hasOwn(object, "retryAfter")) {
    if (typeof object["retryAfter"] !== 'number' || !Number.isInteger(object["retryAfter"])) throw new Error(`${path}.retryAfter` + ': expected integer')
    if ((object["retryAfter"] as number) < 0) throw new Error(`${path}.retryAfter` + ': below minimum 0')
    if ((object["retryAfter"] as number) > 9007199254740991) throw new Error(`${path}.retryAfter` + ': above maximum 9007199254740991')
  }
}

function assertUsage(value: unknown, path: string): asserts value is Usage {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["unit","quantity","costMicros","tier"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "unit")) throw new Error(`${path}: missing required property unit`)
  if (typeof object["unit"] !== 'string') throw new Error(`${path}.unit` + ': expected string')
  if (!hasOwn(object, "quantity")) throw new Error(`${path}: missing required property quantity`)
  if (typeof object["quantity"] !== 'number' || !Number.isFinite(object["quantity"])) throw new Error(`${path}.quantity` + ': expected number')
  if (hasOwn(object, "costMicros")) {
    if (object["costMicros"] !== null) {
      if (typeof object["costMicros"] !== 'number' || !Number.isInteger(object["costMicros"])) throw new Error(`${path}.costMicros` + ': expected integer')
      if ((object["costMicros"] as number) < 0) throw new Error(`${path}.costMicros` + ': below minimum 0')
      if ((object["costMicros"] as number) > 9007199254740991) throw new Error(`${path}.costMicros` + ': above maximum 9007199254740991')
    }
  }
  if (!hasOwn(object, "tier")) throw new Error(`${path}: missing required property tier`)
  if (!["local","remote","cloud"].includes(object["tier"] as never)) throw new Error(`${path}.tier` + ': value is outside enum')
}

function assertArtifact(value: unknown, path: string): asserts value is Artifact {
  const object = protocolObject(value, path)
  if (!hasOwn(object, "kind")) throw new Error(`${path}: missing required property kind`)
  if (typeof object["kind"] !== 'string') throw new Error(`${path}.kind` + ': expected string')
  if (hasOwn(object, "uri")) {
    if (typeof object["uri"] !== 'string') throw new Error(`${path}.uri` + ': expected string')
  }
  if (hasOwn(object, "mime")) {
    if (typeof object["mime"] !== 'string') throw new Error(`${path}.mime` + ': expected string')
  }
  if (hasOwn(object, "text")) {
    if (typeof object["text"] !== 'string') throw new Error(`${path}.text` + ': expected string')
  }
}

function assertCapabilityResult(value: unknown, path: string): asserts value is CapabilityResult {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["jobId","status","progress","artifacts","usage","error"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "jobId")) throw new Error(`${path}: missing required property jobId`)
  if (typeof object["jobId"] !== 'string') throw new Error(`${path}.jobId` + ': expected string')
  if (!hasOwn(object, "status")) throw new Error(`${path}: missing required property status`)
  if (!["accepted","running","succeeded","partial","failed"].includes(object["status"] as never)) throw new Error(`${path}.status` + ': value is outside enum')
  if (!hasOwn(object, "progress")) throw new Error(`${path}: missing required property progress`)
  if (typeof object["progress"] !== 'number' || !Number.isFinite(object["progress"])) throw new Error(`${path}.progress` + ': expected number')
  if ((object["progress"] as number) < 0) throw new Error(`${path}.progress` + ': below minimum 0')
  if ((object["progress"] as number) > 1) throw new Error(`${path}.progress` + ': above maximum 1')
  if (!hasOwn(object, "artifacts")) throw new Error(`${path}: missing required property artifacts`)
  if (!Array.isArray(object["artifacts"])) throw new Error(`${path}.artifacts` + ': expected array')
  for (let index = 0; index < object["artifacts"].length; index += 1) {
    assertArtifact(object["artifacts"][index], `${path}.artifacts` + '[' + index + ']')
  }
  if (!hasOwn(object, "usage")) throw new Error(`${path}: missing required property usage`)
  assertUsage(object["usage"], `${path}.usage`)
  if (!hasOwn(object, "error")) throw new Error(`${path}: missing required property error`)
  if (object["error"] !== null) assertProtocolError(object["error"], `${path}.error`)
}

function assertRuntimeHealth(value: unknown, path: string): asserts value is RuntimeHealth {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["runtimeId","capability","state","detail","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "runtimeId")) throw new Error(`${path}: missing required property runtimeId`)
  if (typeof object["runtimeId"] !== 'string') throw new Error(`${path}.runtimeId` + ': expected string')
  if (!hasOwn(object, "capability")) throw new Error(`${path}: missing required property capability`)
  if (typeof object["capability"] !== 'string') throw new Error(`${path}.capability` + ': expected string')
  if (!hasOwn(object, "state")) throw new Error(`${path}: missing required property state`)
  if (!["up","degraded","down"].includes(object["state"] as never)) throw new Error(`${path}.state` + ': value is outside enum')
  if (!hasOwn(object, "detail")) throw new Error(`${path}: missing required property detail`)
  if (object["detail"] !== null) {
    if (typeof object["detail"] !== 'string') throw new Error(`${path}.detail` + ': expected string')
  }
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

function assertHealthReport(value: unknown, path: string): asserts value is HealthReport {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["runtimes"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "runtimes")) throw new Error(`${path}: missing required property runtimes`)
  if (!Array.isArray(object["runtimes"])) throw new Error(`${path}.runtimes` + ': expected array')
  for (let index = 0; index < object["runtimes"].length; index += 1) {
    assertRuntimeHealth(object["runtimes"][index], `${path}.runtimes` + '[' + index + ']')
  }
}

function assertProviderDescriptor(value: unknown, path: string): asserts value is ProviderDescriptor {
  const object = protocolObject(value, path)
  if (!hasOwn(object, "id")) throw new Error(`${path}: missing required property id`)
  if (typeof object["id"] !== 'string') throw new Error(`${path}.id` + ': expected string')
}

function assertRuntimeCapability(value: unknown, path: string): asserts value is RuntimeCapability {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["capabilityId","schemaVersion","providers","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "capabilityId")) throw new Error(`${path}: missing required property capabilityId`)
  if (typeof object["capabilityId"] !== 'string') throw new Error(`${path}.capabilityId` + ': expected string')
  if (!hasOwn(object, "schemaVersion")) throw new Error(`${path}: missing required property schemaVersion`)
  if (typeof object["schemaVersion"] !== 'string') throw new Error(`${path}.schemaVersion` + ': expected string')
  if (!hasOwn(object, "providers")) throw new Error(`${path}: missing required property providers`)
  if (!Array.isArray(object["providers"])) throw new Error(`${path}.providers` + ': expected array')
  for (let index = 0; index < object["providers"].length; index += 1) {
    assertProviderDescriptor(object["providers"][index], `${path}.providers` + '[' + index + ']')
  }
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

function assertAnnounceRequest(value: unknown, path: string): asserts value is AnnounceRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["runtimeId"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "runtimeId")) throw new Error(`${path}: missing required property runtimeId`)
  if (typeof object["runtimeId"] !== 'string') throw new Error(`${path}.runtimeId` + ': expected string')
}

function assertAnnounceResult(value: unknown, path: string): asserts value is AnnounceResult {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["runtimeId","name","contractVersion","capabilities","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "runtimeId")) throw new Error(`${path}: missing required property runtimeId`)
  if (typeof object["runtimeId"] !== 'string') throw new Error(`${path}.runtimeId` + ': expected string')
  if (!hasOwn(object, "name")) throw new Error(`${path}: missing required property name`)
  if (typeof object["name"] !== 'string') throw new Error(`${path}.name` + ': expected string')
  if (!hasOwn(object, "contractVersion")) throw new Error(`${path}: missing required property contractVersion`)
  if (typeof object["contractVersion"] !== 'string') throw new Error(`${path}.contractVersion` + ': expected string')
  if (!hasOwn(object, "capabilities")) throw new Error(`${path}: missing required property capabilities`)
  if (!Array.isArray(object["capabilities"])) throw new Error(`${path}.capabilities` + ': expected array')
  for (let index = 0; index < object["capabilities"].length; index += 1) {
    assertRuntimeCapability(object["capabilities"][index], `${path}.capabilities` + '[' + index + ']')
  }
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

function assertNegotiateRequest(value: unknown, path: string): asserts value is NegotiateRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["runtimeId","contractVersion","capabilitySchemaVersions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "runtimeId")) throw new Error(`${path}: missing required property runtimeId`)
  if (typeof object["runtimeId"] !== 'string') throw new Error(`${path}.runtimeId` + ': expected string')
  if (!hasOwn(object, "contractVersion")) throw new Error(`${path}: missing required property contractVersion`)
  if (typeof object["contractVersion"] !== 'string') throw new Error(`${path}.contractVersion` + ': expected string')
  if (!hasOwn(object, "capabilitySchemaVersions")) throw new Error(`${path}: missing required property capabilitySchemaVersions`)
  const map3391 = protocolObject(object["capabilitySchemaVersions"], `${path}.capabilitySchemaVersions`)
  for (const [key, entry] of Object.entries(map3391)) {
    if (typeof entry !== 'string') throw new Error(`${path}.capabilitySchemaVersions` + '.' + key + ': expected string')
  }
}

function assertNegotiateResult(value: unknown, path: string): asserts value is NegotiateResult {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["compatible","agreedContractVersion","acceptedCapabilities","reason"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "compatible")) throw new Error(`${path}: missing required property compatible`)
  if (typeof object["compatible"] !== 'boolean') throw new Error(`${path}.compatible` + ': expected boolean')
  if (!hasOwn(object, "agreedContractVersion")) throw new Error(`${path}: missing required property agreedContractVersion`)
  if (typeof object["agreedContractVersion"] !== 'string') throw new Error(`${path}.agreedContractVersion` + ': expected string')
  if (!hasOwn(object, "acceptedCapabilities")) throw new Error(`${path}: missing required property acceptedCapabilities`)
  if (!Array.isArray(object["acceptedCapabilities"])) throw new Error(`${path}.acceptedCapabilities` + ': expected array')
  for (let index = 0; index < object["acceptedCapabilities"].length; index += 1) {
    if (typeof object["acceptedCapabilities"][index] !== 'string') throw new Error(`${path}.acceptedCapabilities` + '[' + index + ']' + ': expected string')
  }
  if (!hasOwn(object, "reason")) throw new Error(`${path}: missing required property reason`)
  if (object["reason"] !== null) {
    if (typeof object["reason"] !== 'string') throw new Error(`${path}.reason` + ': expected string')
  }
}

function assertAddressRequest(value: unknown, path: string): asserts value is AddressRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["runtimeId"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "runtimeId")) throw new Error(`${path}: missing required property runtimeId`)
  if (typeof object["runtimeId"] !== 'string') throw new Error(`${path}.runtimeId` + ': expected string')
}

function assertAddressResult(value: unknown, path: string): asserts value is AddressResult {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["runtimeId","mode","baseUrl","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "runtimeId")) throw new Error(`${path}: missing required property runtimeId`)
  if (typeof object["runtimeId"] !== 'string') throw new Error(`${path}.runtimeId` + ': expected string')
  if (!hasOwn(object, "mode")) throw new Error(`${path}: missing required property mode`)
  if (!["in-process","local-subprocess","remote-mesh","relay"].includes(object["mode"] as never)) throw new Error(`${path}.mode` + ': value is outside enum')
  if (hasOwn(object, "baseUrl")) {
    if (typeof object["baseUrl"] !== 'string') throw new Error(`${path}.baseUrl` + ': expected string')
  }
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

function assertSecureRequest(value: unknown, path: string): asserts value is SecureRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["operationId","capabilityId","correlationId"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "operationId")) throw new Error(`${path}: missing required property operationId`)
  if (typeof object["operationId"] !== 'string') throw new Error(`${path}.operationId` + ': expected string')
  if (!hasOwn(object, "capabilityId")) throw new Error(`${path}: missing required property capabilityId`)
  if (typeof object["capabilityId"] !== 'string') throw new Error(`${path}.capabilityId` + ': expected string')
  if (!hasOwn(object, "correlationId")) throw new Error(`${path}: missing required property correlationId`)
  if (typeof object["correlationId"] !== 'string') throw new Error(`${path}.correlationId` + ': expected string')
}

function assertSecureResult(value: unknown, path: string): asserts value is SecureResult {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["allowed","authority","reason"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "allowed")) throw new Error(`${path}: missing required property allowed`)
  if (typeof object["allowed"] !== 'boolean') throw new Error(`${path}.allowed` + ': expected boolean')
  if (!hasOwn(object, "authority")) throw new Error(`${path}: missing required property authority`)
  if (!["AP","CP"].includes(object["authority"] as never)) throw new Error(`${path}.authority` + ': value is outside enum')
  if (!hasOwn(object, "reason")) throw new Error(`${path}: missing required property reason`)
  if (object["reason"] !== null) {
    if (typeof object["reason"] !== 'string') throw new Error(`${path}.reason` + ': expected string')
  }
}

function assertCapabilityInvokeRequest(value: unknown, path: string): asserts value is CapabilityInvokeRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["capability","core","correlationId","idempotencyKey"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "capability")) throw new Error(`${path}: missing required property capability`)
  if (typeof object["capability"] !== 'string') throw new Error(`${path}.capability` + ': expected string')
  if (!hasOwn(object, "core")) throw new Error(`${path}: missing required property core`)
  protocolObject(object["core"], `${path}.core`)
  if (!hasOwn(object, "correlationId")) throw new Error(`${path}: missing required property correlationId`)
  if (typeof object["correlationId"] !== 'string') throw new Error(`${path}.correlationId` + ': expected string')
  if (!hasOwn(object, "idempotencyKey")) throw new Error(`${path}: missing required property idempotencyKey`)
  if (typeof object["idempotencyKey"] !== 'string') throw new Error(`${path}.idempotencyKey` + ': expected string')
}

function assertCapabilityHostInvokeRequest(value: unknown, path: string): asserts value is CapabilityHostInvokeRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["capability","core"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "capability")) throw new Error(`${path}: missing required property capability`)
  if (typeof object["capability"] !== 'string') throw new Error(`${path}.capability` + ': expected string')
  if (!hasOwn(object, "core")) throw new Error(`${path}: missing required property core`)
  protocolObject(object["core"], `${path}.core`)
}

function assertObserveRequest(value: unknown, path: string): asserts value is ObserveRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["runtimeId","kind"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (hasOwn(object, "runtimeId")) {
    if (typeof object["runtimeId"] !== 'string') throw new Error(`${path}.runtimeId` + ': expected string')
  }
  if (!hasOwn(object, "kind")) throw new Error(`${path}: missing required property kind`)
  if (!["startup","liveness","readiness"].includes(object["kind"] as never)) throw new Error(`${path}.kind` + ': value is outside enum')
}

function assertResolveRequest(value: unknown, path: string): asserts value is ResolveRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["capabilityId"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "capabilityId")) throw new Error(`${path}: missing required property capabilityId`)
  if (typeof object["capabilityId"] !== 'string') throw new Error(`${path}.capabilityId` + ': expected string')
}

function assertResolutionResult(value: unknown, path: string): asserts value is ResolutionResult {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["chosenProviderId","tier","resolutionState","selectionReason","speedHint","reason","isFallback","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "chosenProviderId")) throw new Error(`${path}: missing required property chosenProviderId`)
  if (object["chosenProviderId"] !== null) {
    if (typeof object["chosenProviderId"] !== 'string') throw new Error(`${path}.chosenProviderId` + ': expected string')
  }
  if (!hasOwn(object, "tier")) throw new Error(`${path}: missing required property tier`)
  if (!["local","remote","cloud"].includes(object["tier"] as never)) throw new Error(`${path}.tier` + ': value is outside enum')
  if (!hasOwn(object, "resolutionState")) throw new Error(`${path}: missing required property resolutionState`)
  if (!["available","locked-entitlement","upsell","unavailable-hardware","degraded","not-in-edition"].includes(object["resolutionState"] as never)) throw new Error(`${path}.resolutionState` + ': value is outside enum')
  if (!hasOwn(object, "selectionReason")) throw new Error(`${path}: missing required property selectionReason`)
  if (!["only-candidate","preferred-by-policy","hardware-fit","fallback-floor","entitlement-gated"].includes(object["selectionReason"] as never)) throw new Error(`${path}.selectionReason` + ': value is outside enum')
  if (!hasOwn(object, "speedHint")) throw new Error(`${path}: missing required property speedHint`)
  if (!["fast","moderate","slow"].includes(object["speedHint"] as never)) throw new Error(`${path}.speedHint` + ': value is outside enum')
  if (!hasOwn(object, "reason")) throw new Error(`${path}: missing required property reason`)
  if (typeof object["reason"] !== 'string') throw new Error(`${path}.reason` + ': expected string')
  if (!hasOwn(object, "isFallback")) throw new Error(`${path}: missing required property isFallback`)
  if (typeof object["isFallback"] !== 'boolean') throw new Error(`${path}.isFallback` + ': expected boolean')
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

function assertComposeRequest(value: unknown, path: string): asserts value is ComposeRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["editionId","capabilityIds"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "editionId")) throw new Error(`${path}: missing required property editionId`)
  if (typeof object["editionId"] !== 'string') throw new Error(`${path}.editionId` + ': expected string')
  if (!hasOwn(object, "capabilityIds")) throw new Error(`${path}: missing required property capabilityIds`)
  if (!Array.isArray(object["capabilityIds"])) throw new Error(`${path}.capabilityIds` + ': expected array')
  for (let index = 0; index < object["capabilityIds"].length; index += 1) {
    if (typeof object["capabilityIds"][index] !== 'string') throw new Error(`${path}.capabilityIds` + '[' + index + ']' + ': expected string')
  }
}

function assertComposeResult(value: unknown, path: string): asserts value is ComposeResult {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["editionId","acceptedCapabilityIds","rejectedCapabilityIds"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "editionId")) throw new Error(`${path}: missing required property editionId`)
  if (typeof object["editionId"] !== 'string') throw new Error(`${path}.editionId` + ': expected string')
  if (!hasOwn(object, "acceptedCapabilityIds")) throw new Error(`${path}: missing required property acceptedCapabilityIds`)
  if (!Array.isArray(object["acceptedCapabilityIds"])) throw new Error(`${path}.acceptedCapabilityIds` + ': expected array')
  for (let index = 0; index < object["acceptedCapabilityIds"].length; index += 1) {
    if (typeof object["acceptedCapabilityIds"][index] !== 'string') throw new Error(`${path}.acceptedCapabilityIds` + '[' + index + ']' + ': expected string')
  }
  if (!hasOwn(object, "rejectedCapabilityIds")) throw new Error(`${path}: missing required property rejectedCapabilityIds`)
  if (!Array.isArray(object["rejectedCapabilityIds"])) throw new Error(`${path}.rejectedCapabilityIds` + ': expected array')
  for (let index = 0; index < object["rejectedCapabilityIds"].length; index += 1) {
    if (typeof object["rejectedCapabilityIds"][index] !== 'string') throw new Error(`${path}.rejectedCapabilityIds` + '[' + index + ']' + ': expected string')
  }
}

function assertCpDemoRequest(value: unknown, path: string): asserts value is CpDemoRequest {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["note"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "note")) throw new Error(`${path}: missing required property note`)
  if (object["note"] !== null) {
    if (typeof object["note"] !== 'string') throw new Error(`${path}.note` + ': expected string')
  }
}

function assertCpDemoMeta(value: unknown, path: string): asserts value is CpDemoMeta {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["command","confirmed","note","executedAt","proposedBy","confirmedBy"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "command")) throw new Error(`${path}: missing required property command`)
  if (!["demo-cp-op"].includes(object["command"] as never)) throw new Error(`${path}.command` + ': value is outside enum')
  if (!hasOwn(object, "confirmed")) throw new Error(`${path}: missing required property confirmed`)
  if (typeof object["confirmed"] !== 'boolean') throw new Error(`${path}.confirmed` + ': expected boolean')
  if (!hasOwn(object, "note")) throw new Error(`${path}: missing required property note`)
  if (typeof object["note"] !== 'string') throw new Error(`${path}.note` + ': expected string')
  if (!hasOwn(object, "executedAt")) throw new Error(`${path}: missing required property executedAt`)
  if (typeof object["executedAt"] !== 'string') throw new Error(`${path}.executedAt` + ': expected string')
  if (!hasOwn(object, "proposedBy")) throw new Error(`${path}: missing required property proposedBy`)
  assertPrincipal(object["proposedBy"], `${path}.proposedBy`)
  if (!hasOwn(object, "confirmedBy")) throw new Error(`${path}: missing required property confirmedBy`)
  assertPrincipal(object["confirmedBy"], `${path}.confirmedBy`)
}

function assertCpDemoResult(value: unknown, path: string): asserts value is CpDemoResult {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["jobId","status","progress","artifacts","usage","error","meta"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "jobId")) throw new Error(`${path}: missing required property jobId`)
  if (typeof object["jobId"] !== 'string') throw new Error(`${path}.jobId` + ': expected string')
  if (!hasOwn(object, "status")) throw new Error(`${path}: missing required property status`)
  if (!["accepted","running","succeeded","partial","failed"].includes(object["status"] as never)) throw new Error(`${path}.status` + ': value is outside enum')
  if (!hasOwn(object, "progress")) throw new Error(`${path}: missing required property progress`)
  if (typeof object["progress"] !== 'number' || !Number.isFinite(object["progress"])) throw new Error(`${path}.progress` + ': expected number')
  if ((object["progress"] as number) < 0) throw new Error(`${path}.progress` + ': below minimum 0')
  if ((object["progress"] as number) > 1) throw new Error(`${path}.progress` + ': above maximum 1')
  if (!hasOwn(object, "artifacts")) throw new Error(`${path}: missing required property artifacts`)
  if (!Array.isArray(object["artifacts"])) throw new Error(`${path}.artifacts` + ': expected array')
  for (let index = 0; index < object["artifacts"].length; index += 1) {
    assertArtifact(object["artifacts"][index], `${path}.artifacts` + '[' + index + ']')
  }
  if (!hasOwn(object, "usage")) throw new Error(`${path}: missing required property usage`)
  assertUsage(object["usage"], `${path}.usage`)
  if (!hasOwn(object, "error")) throw new Error(`${path}: missing required property error`)
  if (object["error"] !== null) assertProtocolError(object["error"], `${path}.error`)
  if (hasOwn(object, "meta")) {
    assertCpDemoMeta(object["meta"], `${path}.meta`)
  }
}

function assertNodeStatus(value: unknown, path: string): asserts value is NodeStatus {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["state","baseUrl","sessionToken","detail","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "state")) throw new Error(`${path}: missing required property state`)
  if (!["starting","running","failed","stopped"].includes(object["state"] as never)) throw new Error(`${path}.state` + ': value is outside enum')
  if (!hasOwn(object, "baseUrl")) throw new Error(`${path}: missing required property baseUrl`)
  if (object["baseUrl"] !== null) {
    if (typeof object["baseUrl"] !== 'string') throw new Error(`${path}.baseUrl` + ': expected string')
  }
  if (!hasOwn(object, "sessionToken")) throw new Error(`${path}: missing required property sessionToken`)
  if (object["sessionToken"] !== null) {
    if (typeof object["sessionToken"] !== 'string') throw new Error(`${path}.sessionToken` + ': expected string')
  }
  if (hasOwn(object, "detail")) {
    if (typeof object["detail"] !== 'string') throw new Error(`${path}.detail` + ': expected string')
  }
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

function assertBackupStatus(value: unknown, path: string): asserts value is BackupStatus {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["state","destination","lastSuccessfulBackupAtMs"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "state")) throw new Error(`${path}: missing required property state`)
  if (!["notConfigured","configured"].includes(object["state"] as never)) throw new Error(`${path}.state` + ': value is outside enum')
  if (!hasOwn(object, "destination")) throw new Error(`${path}: missing required property destination`)
  if (object["destination"] !== null) {
    if (typeof object["destination"] !== 'string') throw new Error(`${path}.destination` + ': expected string')
  }
  if (!hasOwn(object, "lastSuccessfulBackupAtMs")) throw new Error(`${path}: missing required property lastSuccessfulBackupAtMs`)
  if (object["lastSuccessfulBackupAtMs"] !== null) {
    if (typeof object["lastSuccessfulBackupAtMs"] !== 'number' || !Number.isInteger(object["lastSuccessfulBackupAtMs"])) throw new Error(`${path}.lastSuccessfulBackupAtMs` + ': expected integer')
    if ((object["lastSuccessfulBackupAtMs"] as number) < 0) throw new Error(`${path}.lastSuccessfulBackupAtMs` + ': below minimum 0')
    if ((object["lastSuccessfulBackupAtMs"] as number) > 9007199254740991) throw new Error(`${path}.lastSuccessfulBackupAtMs` + ': above maximum 9007199254740991')
  }
}

function assertDataLocationStatus(value: unknown, path: string): asserts value is DataLocationStatus {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["dataDirectory","dataDirectorySource","backup","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "dataDirectory")) throw new Error(`${path}: missing required property dataDirectory`)
  if (typeof object["dataDirectory"] !== 'string') throw new Error(`${path}.dataDirectory` + ': expected string')
  if (!hasOwn(object, "dataDirectorySource")) throw new Error(`${path}: missing required property dataDirectorySource`)
  if (!["applicationData","developmentOverride"].includes(object["dataDirectorySource"] as never)) throw new Error(`${path}.dataDirectorySource` + ': value is outside enum')
  if (!hasOwn(object, "backup")) throw new Error(`${path}: missing required property backup`)
  assertBackupStatus(object["backup"], `${path}.backup`)
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

function assertDeviceCapabilityProfile(value: unknown, path: string): asserts value is DeviceCapabilityProfile {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["schemaVersion","detectedAtMs","osFamily","architecture","systemMemoryBytes","fastMemoryBytes","fastMemoryKind","bandwidthClass","bandwidthEvidence","maximumLocalAiTier","detectionStatus","limitations","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "schemaVersion")) throw new Error(`${path}: missing required property schemaVersion`)
  if (![1].includes(object["schemaVersion"] as never)) throw new Error(`${path}.schemaVersion` + ': value is outside enum')
  if ((object["schemaVersion"] as number) > 9007199254740991) throw new Error(`${path}.schemaVersion` + ': above maximum 9007199254740991')
  if (!hasOwn(object, "detectedAtMs")) throw new Error(`${path}: missing required property detectedAtMs`)
  if (typeof object["detectedAtMs"] !== 'number' || !Number.isInteger(object["detectedAtMs"])) throw new Error(`${path}.detectedAtMs` + ': expected integer')
  if ((object["detectedAtMs"] as number) < 0) throw new Error(`${path}.detectedAtMs` + ': below minimum 0')
  if ((object["detectedAtMs"] as number) > 9007199254740991) throw new Error(`${path}.detectedAtMs` + ': above maximum 9007199254740991')
  if (!hasOwn(object, "osFamily")) throw new Error(`${path}: missing required property osFamily`)
  if (!["macos","windows","linux","ios","android","unknown"].includes(object["osFamily"] as never)) throw new Error(`${path}.osFamily` + ': value is outside enum')
  if (!hasOwn(object, "architecture")) throw new Error(`${path}: missing required property architecture`)
  if (typeof object["architecture"] !== 'string') throw new Error(`${path}.architecture` + ': expected string')
  if (!hasOwn(object, "systemMemoryBytes")) throw new Error(`${path}: missing required property systemMemoryBytes`)
  if (object["systemMemoryBytes"] !== null) {
    if (typeof object["systemMemoryBytes"] !== 'number' || !Number.isInteger(object["systemMemoryBytes"])) throw new Error(`${path}.systemMemoryBytes` + ': expected integer')
    if ((object["systemMemoryBytes"] as number) < 0) throw new Error(`${path}.systemMemoryBytes` + ': below minimum 0')
    if ((object["systemMemoryBytes"] as number) > 9007199254740991) throw new Error(`${path}.systemMemoryBytes` + ': above maximum 9007199254740991')
  }
  if (!hasOwn(object, "fastMemoryBytes")) throw new Error(`${path}: missing required property fastMemoryBytes`)
  if (object["fastMemoryBytes"] !== null) {
    if (typeof object["fastMemoryBytes"] !== 'number' || !Number.isInteger(object["fastMemoryBytes"])) throw new Error(`${path}.fastMemoryBytes` + ': expected integer')
    if ((object["fastMemoryBytes"] as number) < 0) throw new Error(`${path}.fastMemoryBytes` + ': below minimum 0')
    if ((object["fastMemoryBytes"] as number) > 9007199254740991) throw new Error(`${path}.fastMemoryBytes` + ': above maximum 9007199254740991')
  }
  if (!hasOwn(object, "fastMemoryKind")) throw new Error(`${path}: missing required property fastMemoryKind`)
  if (!["unified","dedicatedVram","system","unknown"].includes(object["fastMemoryKind"] as never)) throw new Error(`${path}.fastMemoryKind` + ': value is outside enum')
  if (!hasOwn(object, "bandwidthClass")) throw new Error(`${path}: missing required property bandwidthClass`)
  if (!["low","moderate","high","unknown"].includes(object["bandwidthClass"] as never)) throw new Error(`${path}.bandwidthClass` + ': value is outside enum')
  if (!hasOwn(object, "bandwidthEvidence")) throw new Error(`${path}: missing required property bandwidthEvidence`)
  if (!["inferred","unknown"].includes(object["bandwidthEvidence"] as never)) throw new Error(`${path}.bandwidthEvidence` + ': value is outside enum')
  if (!hasOwn(object, "maximumLocalAiTier")) throw new Error(`${path}: missing required property maximumLocalAiTier`)
  if (!["T-E","T-S","T-A","T-R","T-C"].includes(object["maximumLocalAiTier"] as never)) throw new Error(`${path}.maximumLocalAiTier` + ': value is outside enum')
  if (!hasOwn(object, "detectionStatus")) throw new Error(`${path}: missing required property detectionStatus`)
  if (!["complete","partial","unknown"].includes(object["detectionStatus"] as never)) throw new Error(`${path}.detectionStatus` + ': value is outside enum')
  if (!hasOwn(object, "limitations")) throw new Error(`${path}: missing required property limitations`)
  if (!Array.isArray(object["limitations"])) throw new Error(`${path}.limitations` + ': expected array')
  for (let index = 0; index < object["limitations"].length; index += 1) {
    if (!["bandwidthUnavailable","dedicatedVramUnavailable","fastMemoryUnavailable","hostUnavailable","invalidHostResponse","mobileWorkingSetEstimated","probeFailed"].includes(object["limitations"][index] as never)) throw new Error(`${path}.limitations` + '[' + index + ']' + ': value is outside enum')
  }
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

function assertPeerSyncConfig(value: unknown, path: string): asserts value is PeerSyncConfig {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["enableMdns","peers","bindAddress"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "enableMdns")) throw new Error(`${path}: missing required property enableMdns`)
  if (object["enableMdns"] !== null) {
    if (typeof object["enableMdns"] !== 'boolean') throw new Error(`${path}.enableMdns` + ': expected boolean')
  }
  if (!hasOwn(object, "peers")) throw new Error(`${path}: missing required property peers`)
  if (!Array.isArray(object["peers"])) throw new Error(`${path}.peers` + ': expected array')
  for (let index = 0; index < object["peers"].length; index += 1) {
    if (typeof object["peers"][index] !== 'string') throw new Error(`${path}.peers` + '[' + index + ']' + ': expected string')
  }
  if (!hasOwn(object, "bindAddress")) throw new Error(`${path}: missing required property bindAddress`)
  if (object["bindAddress"] !== null) {
    if (typeof object["bindAddress"] !== 'string') throw new Error(`${path}.bindAddress` + ': expected string')
  }
}

function assertRendererLogEntry(value: unknown, path: string): asserts value is RendererLogEntry {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["level","message","stack"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "level")) throw new Error(`${path}: missing required property level`)
  if (typeof object["level"] !== 'string') throw new Error(`${path}.level` + ': expected string')
  if (!hasOwn(object, "message")) throw new Error(`${path}: missing required property message`)
  if (typeof object["message"] !== 'string') throw new Error(`${path}.message` + ': expected string')
  if (hasOwn(object, "stack")) {
    if (typeof object["stack"] !== 'string') throw new Error(`${path}.stack` + ': expected string')
  }
}

function assertSyncPeerStatus(value: unknown, path: string): asserts value is SyncPeerStatus {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["deviceId","label","state","lastReachedAt","offlineDurationMs","isSecurityEvent","errorCode"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "deviceId")) throw new Error(`${path}: missing required property deviceId`)
  if (typeof object["deviceId"] !== 'string') throw new Error(`${path}.deviceId` + ': expected string')
  if (!hasOwn(object, "label")) throw new Error(`${path}: missing required property label`)
  if (typeof object["label"] !== 'string') throw new Error(`${path}.label` + ': expected string')
  if (!hasOwn(object, "state")) throw new Error(`${path}: missing required property state`)
  if (!["has","will","should","couldnt"].includes(object["state"] as never)) throw new Error(`${path}.state` + ': value is outside enum')
  if (!hasOwn(object, "lastReachedAt")) throw new Error(`${path}: missing required property lastReachedAt`)
  if (object["lastReachedAt"] !== null) {
    if (typeof object["lastReachedAt"] !== 'string') throw new Error(`${path}.lastReachedAt` + ': expected string')
  }
  if (!hasOwn(object, "offlineDurationMs")) throw new Error(`${path}: missing required property offlineDurationMs`)
  if (object["offlineDurationMs"] !== null) {
    if (typeof object["offlineDurationMs"] !== 'number' || !Number.isInteger(object["offlineDurationMs"])) throw new Error(`${path}.offlineDurationMs` + ': expected integer')
    if ((object["offlineDurationMs"] as number) < 0) throw new Error(`${path}.offlineDurationMs` + ': below minimum 0')
    if ((object["offlineDurationMs"] as number) > 9007199254740991) throw new Error(`${path}.offlineDurationMs` + ': above maximum 9007199254740991')
  }
  if (!hasOwn(object, "isSecurityEvent")) throw new Error(`${path}: missing required property isSecurityEvent`)
  if (typeof object["isSecurityEvent"] !== 'boolean') throw new Error(`${path}.isSecurityEvent` + ': expected boolean')
  if (!hasOwn(object, "errorCode")) throw new Error(`${path}: missing required property errorCode`)
  if (object["errorCode"] !== null) {
    if (typeof object["errorCode"] !== 'string') throw new Error(`${path}.errorCode` + ': expected string')
  }
}

function assertSyncCadence(value: unknown, path: string): asserts value is SyncCadence {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["nextRoundAt","roundIntervalSeconds"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "nextRoundAt")) throw new Error(`${path}: missing required property nextRoundAt`)
  if (object["nextRoundAt"] !== null) {
    if (typeof object["nextRoundAt"] !== 'string') throw new Error(`${path}.nextRoundAt` + ': expected string')
  }
  if (!hasOwn(object, "roundIntervalSeconds")) throw new Error(`${path}: missing required property roundIntervalSeconds`)
  if (typeof object["roundIntervalSeconds"] !== 'number' || !Number.isInteger(object["roundIntervalSeconds"])) throw new Error(`${path}.roundIntervalSeconds` + ': expected integer')
  if ((object["roundIntervalSeconds"] as number) < 0) throw new Error(`${path}.roundIntervalSeconds` + ': below minimum 0')
  if ((object["roundIntervalSeconds"] as number) > 9007199254740991) throw new Error(`${path}.roundIntervalSeconds` + ': above maximum 9007199254740991')
}

function assertEnrollmentStatus(value: unknown, path: string): asserts value is EnrollmentStatus {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["complete","joinedTeamId","reason"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "complete")) throw new Error(`${path}: missing required property complete`)
  if (typeof object["complete"] !== 'boolean') throw new Error(`${path}.complete` + ': expected boolean')
  if (!hasOwn(object, "joinedTeamId")) throw new Error(`${path}: missing required property joinedTeamId`)
  if (object["joinedTeamId"] !== null) {
    if (typeof object["joinedTeamId"] !== 'string') throw new Error(`${path}.joinedTeamId` + ': expected string')
  }
  if (!hasOwn(object, "reason")) throw new Error(`${path}: missing required property reason`)
  if (object["reason"] !== null) {
    if (typeof object["reason"] !== 'string') throw new Error(`${path}.reason` + ': expected string')
  }
}

function assertLastPeerExchange(value: unknown, path: string): asserts value is LastPeerExchange {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["peerDeviceId","peerLabel","exchangedAt"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "peerDeviceId")) throw new Error(`${path}: missing required property peerDeviceId`)
  if (typeof object["peerDeviceId"] !== 'string') throw new Error(`${path}.peerDeviceId` + ': expected string')
  if (!hasOwn(object, "peerLabel")) throw new Error(`${path}: missing required property peerLabel`)
  if (typeof object["peerLabel"] !== 'string') throw new Error(`${path}.peerLabel` + ': expected string')
  if (!hasOwn(object, "exchangedAt")) throw new Error(`${path}: missing required property exchangedAt`)
  if (typeof object["exchangedAt"] !== 'string') throw new Error(`${path}.exchangedAt` + ': expected string')
}

function assertSyncRecency(value: unknown, path: string): asserts value is SyncRecency {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["basis","currentness","lastExchange"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "basis")) throw new Error(`${path}: missing required property basis`)
  if (typeof object["basis"] !== 'string') throw new Error(`${path}.basis` + ': expected string')
  if (!hasOwn(object, "currentness")) throw new Error(`${path}: missing required property currentness`)
  if (typeof object["currentness"] !== 'string') throw new Error(`${path}.currentness` + ': expected string')
  if (!hasOwn(object, "lastExchange")) throw new Error(`${path}: missing required property lastExchange`)
  if (object["lastExchange"] !== null) assertLastPeerExchange(object["lastExchange"], `${path}.lastExchange`)
}

function assertHarborlineSyncStatus(value: unknown, path: string): asserts value is HarborlineSyncStatus {
  const object = protocolObject(value, path)
  const allowed = new Set<string>(["aggregate","asOf","peers","cadence","recency","enrollment","extensions"])
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)
  }
  if (!hasOwn(object, "aggregate")) throw new Error(`${path}: missing required property aggregate`)
  if (!["has","will","should","couldnt"].includes(object["aggregate"] as never)) throw new Error(`${path}.aggregate` + ': value is outside enum')
  if (!hasOwn(object, "asOf")) throw new Error(`${path}: missing required property asOf`)
  if (typeof object["asOf"] !== 'string') throw new Error(`${path}.asOf` + ': expected string')
  if (!hasOwn(object, "peers")) throw new Error(`${path}: missing required property peers`)
  if (!Array.isArray(object["peers"])) throw new Error(`${path}.peers` + ': expected array')
  for (let index = 0; index < object["peers"].length; index += 1) {
    assertSyncPeerStatus(object["peers"][index], `${path}.peers` + '[' + index + ']')
  }
  if (!hasOwn(object, "cadence")) throw new Error(`${path}: missing required property cadence`)
  assertSyncCadence(object["cadence"], `${path}.cadence`)
  if (!hasOwn(object, "recency")) throw new Error(`${path}: missing required property recency`)
  assertSyncRecency(object["recency"], `${path}.recency`)
  if (hasOwn(object, "enrollment")) {
    assertEnrollmentStatus(object["enrollment"], `${path}.enrollment`)
  }
  if (hasOwn(object, "extensions")) {
    protocolObject(object["extensions"], `${path}.extensions`)
  }
}

export interface CapabilityPort {
  announce(request: AnnounceRequest): Promise<AnnounceResult>
  negotiate(request: NegotiateRequest): Promise<NegotiateResult>
  address(request: AddressRequest): Promise<AddressResult>
  secure(request: SecureRequest): Promise<SecureResult>
  invoke(request: CapabilityInvokeRequest): Promise<CapabilityResult>
  observe(request: ObserveRequest): Promise<HealthReport>
  resolve(request: ResolveRequest): Promise<ResolutionResult>
  compose(request: ComposeRequest): Promise<ComposeResult>
}

export interface HarborlineHostPort {
  capabilityInvoke(request: CapabilityHostInvokeRequest): Promise<CapabilityResult>
  capabilityHealth(request: EmptyRequest): Promise<HealthReport>
  capabilityCpDemoExecute(request: CpDemoRequest): Promise<CpDemoResult>
  currentPrincipal(request: EmptyRequest): Promise<Principal>
  nodeStatus(request: EmptyRequest): Promise<NodeStatus>
  dataLocationStatus(request: EmptyRequest): Promise<DataLocationStatus>
  deviceCapabilityProfile(request: EmptyRequest): Promise<DeviceCapabilityProfile>
  getPeerSyncConfig(request: EmptyRequest): Promise<PeerSyncConfig>
  setPeerSyncConfig(request: PeerSyncConfig): Promise<EmptyResponse>
  appendRendererLog(request: RendererLogEntry): Promise<EmptyResponse>
}

export interface HarborlineApplicationPort {
  getSyncStatus(request: EmptyRequest): Promise<HarborlineSyncStatus>
}
